using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace RniPanel {
    public sealed class VariantIdentity {
        public int Id;
        public string Uuid;
        public string FileName;
    }
    // Windows ships winsqlite3. This adapter opens the actual current catalog
    // with SQLITE_OPEN_READONLY, uses bound parameters, and never executes writes.
    public static class CatalogReader {
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] file,out IntPtr db,int flags,IntPtr vfs);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_busy_timeout(IntPtr db,int ms);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db,byte[] sql,int length,out IntPtr statement,IntPtr tail);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_bind_text(IntPtr statement,int index,byte[] value,int length,IntPtr destructor);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_bind_int(IntPtr statement,int index,int value);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_int(IntPtr statement,int index);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr statement,int index);
        [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr statement,int index);
        static byte[] Utf8(string s){return Encoding.UTF8.GetBytes(s+"\0");}
        static string Text(IntPtr statement,int index){int n=sqlite3_column_bytes(statement,index);if(n==0)return "";byte[] bytes=new byte[n];Marshal.Copy(sqlite3_column_text(statement,index),bytes,0,n);return Encoding.UTF8.GetString(bytes);}
        static void Ok(int code){if(code!=0)throw new InvalidOperationException("只读图库查询失败，SQLite code="+code);}
        public static VariantIdentity Resolve(string database,string fileName,int zeroBasedIndex) {
            return ResolveCore(database,fileName,zeroBasedIndex);
        }
        public static VariantIdentity ResolveSingle(string database,string fileName) {
            return ResolveCore(database,fileName,null);
        }
        static VariantIdentity ResolveCore(string database,string fileName,int? zeroBasedIndex) {
            IntPtr db=IntPtr.Zero,statement=IntPtr.Zero;
            try {
                Ok(sqlite3_open_v2(Utf8(database),out db,1,IntPtr.Zero));
                Ok(sqlite3_busy_timeout(db,1200));
                string sql="SELECT v.Z_PK,v.ZVARIANTUUID,i.ZIMAGEFILENAME FROM ZVARIANT v JOIN ZIMAGE i ON i.Z_PK=v.ZIMAGE WHERE (?1<0 OR v.ZINDEX=?1) AND (i.ZIMAGEFILENAME=?2 COLLATE NOCASE OR i.ZDISPLAYNAME=?3 COLLATE NOCASE) LIMIT 2";
                byte[] query=Utf8(sql);Ok(sqlite3_prepare_v2(db,query,query.Length,out statement,IntPtr.Zero));
                Ok(sqlite3_bind_int(statement,1,zeroBasedIndex??-1));byte[] name=Encoding.UTF8.GetBytes(fileName);
                Ok(sqlite3_bind_text(statement,2,name,name.Length,new IntPtr(-1)));Ok(sqlite3_bind_text(statement,3,name,name.Length,new IntPtr(-1)));
                var rows=new List<VariantIdentity>();int step;
                while((step=sqlite3_step(statement))==100)rows.Add(new VariantIdentity{Id=sqlite3_column_int(statement,0),Uuid=Text(statement,1),FileName=Text(statement,2)});
                if(step!=101)throw new InvalidOperationException("图库查询未正常结束，未发送。");
                if(rows.Count!=1||String.IsNullOrWhiteSpace(rows[0].Uuid))throw new InvalidOperationException("当前照片存在同名、多变体或未保存记录，暂不能唯一识别，未发送。");
                return rows[0];
            } finally {if(statement!=IntPtr.Zero)sqlite3_finalize(statement);if(db!=IntPtr.Zero)sqlite3_close(db);}
        }
    }
}
