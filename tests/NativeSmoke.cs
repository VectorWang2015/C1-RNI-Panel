// Opt-in integration check. Never built into the panel; refuses other catalogs.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RniPanel;

static class NativeSmoke {
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    const string AppRoot=@"C:\Program Files\Capture One\Capture One";
    static StreamWriter log;
    static readonly Type bridge=typeof(CaptureOneBridge);
    static readonly Type readerType=bridge.GetNestedType("NativeStyleReader",BindingFlags.NonPublic);
    static bool Current(){return (GetAsyncKeyState(0x1b)&0x8000)==0;}
    static void Report(string text){Console.WriteLine(text);if(log!=null){log.WriteLine(DateTimeOffset.Now.ToString("o")+" "+text);log.Flush();}}
    static void Guard(TargetSnapshot target) {
        bridge.GetMethod("RequireActiveWindow",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{target,(Func<bool>)Current});
    }
    static object Reader(TargetSnapshot target) {
        var reader=Activator.CreateInstance(readerType,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{target},null);
        readerType.GetField("OperationGuard").SetValue(reader,(Action)(()=>Guard(target)));
        readerType.GetField("Stage").SetValue(reader,(Action<string>)(s=>{if(s.StartsWith("prepare-style.ready"))Report(s);}));
        return reader;
    }
    static string[] Read(object reader,TargetSnapshot target) {
        Guard(target);
        return (string[])readerType.GetMethod("Read").Invoke(reader,null);
    }
    static IBatchStyleSession Session(StyleBinding binding,TargetSnapshot target,StyleBinding[] bindings) {
        var type=bridge.GetNestedType("NativeBatchSession",BindingFlags.NonPublic);
        return (IBatchStyleSession)Activator.CreateInstance(type,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,
            new object[]{binding,target,AppRoot,(Func<bool>)Current,bindings,(Action<string>)Report},null);
    }
    static async Task Run(string mode) {
        if(Process.GetProcessesByName("RniPanel").Length!=0)throw new InvalidOperationException("Close the panel before this opt-in integration run.");
        var original=CaptureOneBridge.InspectPrimary(AppRoot);
        if(original.DocumentPath!=SafetyPolicy.DemoCatalogPath||original.Modal||!original.Enabled)
            throw new InvalidOperationException("An enabled, freshly confirmed sandbox is required.");
        Report("LIVE sandbox="+original.DocumentPath+" primary="+original.VariantId+" uuid="+original.VariantUuid+" selected="+original.SelectedCount);
        ShowWindow(original.Handle,3);
        if(!SetForegroundWindow(original.Handle))throw new InvalidOperationException("Cannot foreground C1.");
        await Task.Delay(180);Guard(original);
        var catalog=Catalog.Read(Catalog.FindRoots(AppRoot),AppRoot);
        // Exercise the tree even if an old optional shortcut happens to exist.
        var bindings=Shortcuts.CreateBindings(catalog,null).ToArray();
        Func<string,int,StyleBinding> find=(film,level)=>bindings.Single(b=>b.Style.Name==film+" "+level+"%"&&
            catalog.Families.Single(f=>f.Id==b.FamilyId).Grain==false);
        var reader=Reader(original);
        if(mode=="paths") {
            foreach(string film in new[]{"Fuji Natura 1600","Kodak Portra 160 V.5","Kodak Portra 400 V.2"})
                foreach(int level in new[]{25,50,75,100}) {
                    var binding=find(film,level);
                    var state=readerType.GetMethod("StyleState").Invoke(reader,new object[]{binding,(Action)(()=>Guard(original))});
                    Report("PATH PASS "+binding.Style.Name+" state="+state+" sources="+binding.Style.NativeSources.Count);
                }
            var builtIn=find("Kodak Portra 160 V.4",100);
            readerType.GetMethod("StyleState").Invoke(reader,new object[]{builtIn,(Action)(()=>Guard(original))});
            Report("PATH PASS built-in-only "+builtIn.Style.Name);
            return;
        }
        if(mode!="pair"||original.SelectedCount<2)throw new InvalidOperationException("Use paths or pair with at least two sandbox selections.");
        var firstSession=Session(find("Kodak Portra 160 V.5",100),original,bindings);
        await firstSession.BeginPrimaryOnly();
        await firstSession.SelectFirst();
        var first=await firstSession.InspectPrimary();
        if(first.VariantUuid!=original.VariantUuid)throw new InvalidOperationException("This narrow smoke check requires the original primary to be first; no photo changed.");
        if(Read(reader,first).Length!=0)throw new InvalidOperationException("First photo has styles; refuse to replace test baseline.");
        await firstSession.SelectNext();
        var second=await firstSession.InspectPrimary();
        if(second.VariantUuid==first.VariantUuid||Read(reader,second).Length!=0)throw new InvalidOperationException("Second photo is not distinct/empty; no photo changed.");
        Report("PAIR locked first="+first.VariantId+" second="+second.VariantId+"; both empty; selection count unchanged="+original.SelectedCount);
        foreach(string stage in new[]{"160-100","400-25","clear"}) {
            StyleBinding binding=stage=="clear"?null:stage=="160-100"?find("Kodak Portra 160 V.5",100):find("Kodak Portra 400 V.2",25);
            var session=stage=="160-100"?firstSession:Session(binding,original,bindings);
            if(session!=firstSession)await session.BeginPrimaryOnly();
            await session.SelectFirst();
            foreach(var target in new[]{first,second}) {
                var now=await session.InspectPrimary();
                if(now.VariantUuid!=target.VariantUuid)throw new InvalidOperationException("Unexpected test primary.");
                bool sent=await session.ExecutePrimary(now);
                Report("NATIVE CONFIRMED stage="+stage+" id="+now.VariantId+" sent="+sent+" rows="+String.Join(" | ",Read(reader,now)));
                if(target==first)await session.SelectNext();
            }
            if(stage=="400-25") {
                bool sent=await session.ExecutePrimary(await session.InspectPrimary());
                if(sent)throw new InvalidOperationException("Repeat unexpectedly sent.");
                Report("NATIVE CONFIRMED repeat no-send id="+second.VariantId);
            }
        }
        await firstSession.SelectFirst();
        await firstSession.RestoreEditMode();
        var final=CaptureOneBridge.InspectPrimary(AppRoot);
        if(final.SelectedCount!=original.SelectedCount||final.VariantUuid!=original.VariantUuid)
            throw new InvalidOperationException("Restored selection/primary mismatch.");
        Report("PAIR PASS; both test photos cleared; original primary/count/edit mode restored.");
    }
    public static int Main(string[] args) {
        if(args.Length!=2){Console.WriteLine("NativeSmoke paths|pair <new-log-path>");return 2;}
        using(log=new StreamWriter(new FileStream(args[1],FileMode.CreateNew,FileAccess.Write,FileShare.Read)))try {
            Run(args[0]).GetAwaiter().GetResult();return 0;
        }catch(Exception e){Report("STOP: "+e);return 1;}
    }
}
