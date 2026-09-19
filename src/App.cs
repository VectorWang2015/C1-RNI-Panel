using System;
using System.Collections.Generic;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Web.Script.Serialization;

[assembly:System.Reflection.AssemblyVersion("0.6.0.0")]
[assembly:System.Reflection.AssemblyFileVersion("0.6.0.0")]

namespace RniPanel {
    static class Program {
        [STAThread] static void Main(string[] args) {
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using(var instance=new Mutex(true,@"Local\RniPaletteDesktop",out created)) {
                try {
                    int ownId=Process.GetCurrentProcess().Id;
                    bool other=Process.GetProcessesByName("RniPanel").Any(p=>p.Id!=ownId&&!p.HasExited);
                    if(!created||other) {
                        MessageBox.Show("请先关闭其他版本的 RNI Palette，再启动此版本。\n\n不会替你关闭旧程序，也不会修改 C1。","RNI Palette · 单实例保护");return;
                    }
                    Application.ThreadException+=(s,e)=>MessageBox.Show(e.Exception.Message,"RNI Panel — 本次动作已停止",MessageBoxButtons.OK,MessageBoxIcon.Warning);
                    Application.Run(new PanelWindow());
                }finally{if(created)instance.ReleaseMutex();}
            }
        }
    }
    sealed class PanelWindow:Form {
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        readonly Color bg=Color.FromArgb(24,27,30), card=Color.FromArgb(35,39,43), line=Color.FromArgb(62,67,71), ink=Color.FromArgb(237,233,222), mute=Color.FromArgb(161,171,177), accent=Color.FromArgb(232,182,90);
        readonly string appRoot=@"C:\Program Files\Capture One\Capture One";
        readonly PreferencesStore store;
        PanelPreferences preferences;
        StyleCatalog catalog;
        List<StyleBinding> bindings=new List<StyleBinding>();
        TextBox search;
        CheckBox favoritesOnly,pin,live;
        ComboBox category;
        FlowLayoutPanel films;
        Label counts,connection,target,status;
        Button connectButton,lockButton,helpButton,clearButton;
        int armedId;
        TargetSnapshot armedTarget;
        TargetSnapshot connectedCatalog;
        readonly AttemptGate attemptGate=new AttemptGate();
        readonly System.Windows.Forms.Timer stopTimer=new System.Windows.Forms.Timer {Interval=50};
        string confirmedStyleUuid;
        bool busy,booting=true,setupAcknowledged;
        public PanelWindow() {
            Text="RNI Palette · 0.6";Name="RniPaletteWindow";StartPosition=FormStartPosition.CenterScreen;
            Size=new Size(490,820);MinimumSize=new Size(440,640);BackColor=bg;ForeColor=ink;Font=new Font("Microsoft YaHei UI",9F);AutoScaleMode=AutoScaleMode.Dpi;
            store=new PreferencesStore(PreferencesStore.SharedDirectory);
            try {store.ImportIfMissing(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"data","favorites.json"));preferences=store.Load();} catch(Exception e){preferences=new PanelPreferences();Shown+=(s,a)=>MessageBox.Show("收藏未加载；原文件保留。\n"+e.Message,"收藏文件检查");}
            TopMost=preferences.TopMost;
            RestorePlacement();
            BuildUi();
            stopTimer.Tick+=(s,e)=>{
                if(busy&&(GetAsyncKeyState(0x1b)&0x8000)!=0) {
                    attemptGate.Cancel();live.Checked=false;
                    SetStatus("已请求停止；不会继续发送。已执行的动作请以 C1 为准。",true);
                }
            };
            stopTimer.Start();
            FormClosing+=(s,e)=>{stopTimer.Stop();stopTimer.Dispose();attemptGate.Cancel();SavePlacement();};Shown+=async(s,e)=>await LoadCatalog();
        }
        void RestorePlacement() {
            if(!preferences.WindowPlacementSaved||preferences.WindowWidth<440||preferences.WindowHeight<640)return;
            var wanted=new Rectangle(preferences.WindowLeft,preferences.WindowTop,preferences.WindowWidth,preferences.WindowHeight);
            var area=Screen.FromRectangle(wanted).WorkingArea;
            int width=Math.Max(440,Math.Min(wanted.Width,area.Width));int height=Math.Max(640,Math.Min(wanted.Height,area.Height));
            StartPosition=FormStartPosition.Manual;
            Bounds=new Rectangle(Math.Max(area.Left,Math.Min(wanted.Left,area.Right-width)),Math.Max(area.Top,Math.Min(wanted.Top,area.Bottom-height)),width,height);
        }
        void SavePlacement() {
            var bounds=WindowState==FormWindowState.Normal?Bounds:RestoreBounds;
            preferences.WindowPlacementSaved=true;preferences.WindowLeft=bounds.Left;preferences.WindowTop=bounds.Top;
            preferences.WindowWidth=bounds.Width;preferences.WindowHeight=bounds.Height;Save();
        }
        Label MakeLabel(string text,int height,Color color,float size=9F) {return new Label {Text=text,Height=height,ForeColor=color,Font=new Font("Microsoft YaHei UI",size),Dock=DockStyle.Top,Padding=new Padding(0,3,0,0),AutoEllipsis=true};}
        Button MakeButton(string text,int width=100) {
            var button=new Button {Text=text,Width=width,Height=34,FlatStyle=FlatStyle.Flat,BackColor=card,ForeColor=ink,Margin=new Padding(0,0,8,0),Cursor=Cursors.Hand};
            button.FlatAppearance.BorderColor=line;button.FlatAppearance.MouseOverBackColor=Color.FromArgb(55,61,65);return button;
        }
        void BuildUi() {
            var outer=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Padding=new Padding(18)};
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute,178));outer.RowStyles.Add(new RowStyle(SizeType.Absolute,92));outer.RowStyles.Add(new RowStyle(SizeType.Percent,100));outer.RowStyles.Add(new RowStyle(SizeType.Absolute,114));
            Controls.Add(outer);
            var header=new Panel {Dock=DockStyle.Fill};outer.Controls.Add(header,0,0);
            var title=MakeLabel("RNI  /  PALETTE",33,ink,18F);title.Top=0;title.Dock=DockStyle.None;title.Width=360;header.Controls.Add(title);
            var sub=MakeLabel("胶片收藏夹   ·   原生样式 / 清除 RNI 0.6",26,mute);sub.Dock=DockStyle.None;sub.Top=38;sub.Width=360;header.Controls.Add(sub);
            pin=new CheckBox {Text="置顶",Checked=preferences.TopMost,ForeColor=mute,Width=62,Height=28,Anchor=AnchorStyles.Top|AnchorStyles.Right,Left=350,Top=6};header.Controls.Add(pin);
            header.Resize+=(s,e)=>pin.Left=header.ClientSize.Width-62;
            pin.CheckedChanged+=(s,e)=>{TopMost=pin.Checked;preferences.TopMost=pin.Checked;Save();};
            search=new TextBox {Name="FilmSearch",AccessibleName="搜索胶片",Left=0,Top=74,Height=32,Width=420,Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right,BackColor=Color.FromArgb(46,51,56),ForeColor=ink,BorderStyle=BorderStyle.FixedSingle,Font=new Font("Microsoft YaHei UI",11)};
            header.Controls.Add(search);header.Resize+=(s,e)=>search.Width=header.ClientSize.Width;
            search.TextChanged+=(s,e)=>RenderFilms();
            favoritesOnly=new CheckBox {Text="只看收藏",Checked=preferences.FavoritesOnly,Left=0,Top=116,Width=112,Height=27,ForeColor=accent};header.Controls.Add(favoritesOnly);
            favoritesOnly.CheckedChanged+=(s,e)=>{preferences.FavoritesOnly=favoritesOnly.Checked;Save();RenderFilms();};
            category=new ComboBox {Name="CategoryFilter",AccessibleName="样式种类",Left=124,Top=116,Width=285,Height=28,DropDownStyle=ComboBoxStyle.DropDownList,BackColor=card,ForeColor=ink,FlatStyle=FlatStyle.Flat,Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right};
            category.Items.AddRange(new object[]{"已接入 / 可应用","全部类型","标准 / 无颗粒","颗粒版","JPEG / TIFF 专用"});category.SelectedIndex=0;category.SelectedIndexChanged+=(s,e)=>RenderFilms();header.Controls.Add(category);
            header.Resize+=(s,e)=>category.Width=Math.Max(180,header.ClientSize.Width-124);
            counts=MakeLabel("正在只读扫描本机样式…",24,mute,8F);counts.Dock=DockStyle.None;counts.Top=150;counts.Width=420;header.Controls.Add(counts);
            var safety=new Panel {Dock=DockStyle.Fill,BackColor=Color.FromArgb(40,43,41),Padding=new Padding(10)};outer.Controls.Add(safety,0,1);
            connection=MakeLabel("● 预览模式 — 不会修改照片",25,accent,9F);connection.Dock=DockStyle.None;connection.Left=10;connection.Top=6;connection.Width=410;safety.Controls.Add(connection);
            target=MakeLabel("未连接；先在 C1 选照片，再连接当前图库",24,mute,8F);target.Dock=DockStyle.None;target.Left=10;target.Top=32;target.Width=410;safety.Controls.Add(target);
            live=new CheckBox {Text="启用连接（当前选中照片）",Left=10,Top=59,Width=265,Height=25,Enabled=false,ForeColor=ink};safety.Controls.Add(live);
            live.CheckedChanged+=(s,e)=>{attemptGate.Cancel();connection.Text=live.Checked?"● 已连接 — 点击作用于当前选中照片":"● 预览模式 — 不会修改照片";if(clearButton!=null)clearButton.Enabled=live.Checked&&!busy;if(!live.Checked){confirmedStyleUuid=null;UpdateHighlights();}};
            clearButton=MakeButton("清除 RNI",98);clearButton.Name="ClearRniStyle";clearButton.AccessibleName="清除当前 RNI 样式";
            clearButton.Height=28;clearButton.Top=56;clearButton.Left=310;clearButton.Enabled=false;clearButton.Anchor=AnchorStyles.Top|AnchorStyles.Right;
            clearButton.Click+=async(s,e)=>await ClearStyle();safety.Controls.Add(clearButton);
            safety.Resize+=(s,e)=>{clearButton.Left=safety.ClientSize.Width-108;live.Width=Math.Max(190,safety.ClientSize.Width-130);};
            var clearTip=new ToolTip();clearTip.SetToolTip(clearButton,"只移除面板支持的当前 RNI 样式，不执行整张照片重置。");clearButton.Disposed+=(s,e)=>clearTip.Dispose();
            films=new FlowLayoutPanel {Name="FilmCards",Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,BackColor=bg,Padding=new Padding(0,10,0,0)};outer.Controls.Add(films,0,2);
            films.ClientSizeChanged+=(s,e)=>ResizeCards();
            var footer=new Panel {Dock=DockStyle.Fill,Padding=new Padding(0,10,0,0)};outer.Controls.Add(footer,0,3);
            var actions=new FlowLayoutPanel {Dock=DockStyle.Top,Height=43,WrapContents=false};footer.Controls.Add(actions);
            connectButton=MakeButton("连接检查",100);connectButton.Click+=(s,e)=>ShowSetup();actions.Controls.Add(connectButton);
            lockButton=MakeButton("连接当前图库",126);lockButton.Click+=async(s,e)=>await ConnectCatalog();actions.Controls.Add(lockButton);
            helpButton=MakeButton("使用说明",96);helpButton.Click+=(s,e)=>ShowHelp();actions.Controls.Add(helpButton);
            status=MakeLabel("提示：搜索 Portra160；星标只保存在此面板。",57,mute,8F);status.Dock=DockStyle.Bottom;status.AutoEllipsis=false;footer.Controls.Add(status);
        }
        async Task LoadCatalog() {
            try {
                catalog=await Task.Run(()=>Catalog.Read(Catalog.FindRoots(appRoot),appRoot));
                if(IsDisposed||Disposing)return;
                bindings=Shortcuts.CreateBindings(catalog);
                if(!preferences.FavoritesInitialized) {
                    foreach(var name in new[]{"Kodak Portra 160","Kodak Portra 400","Fuji Natura 1600","Agfa RSX II","Agfa RSX II v3"}) {
                        var film=catalog.Families.FirstOrDefault(f=>Catalog.Normalize(f.Name).Replace("v.","v")==Catalog.Normalize(name).Replace("v.","v")&&!f.Grain&&!f.Rendered);
                        if(film!=null&&!preferences.Favorites.Contains(film.Id))preferences.Favorites.Add(film.Id);
                    }
                    preferences.FavoritesInitialized=true;Save();
                }
                booting=false;Save();RenderFilms();SetStatus(String.Format("已索引 {0} 份样式入口；应用时核对原生状态，收藏跨版本保留。",bindings.Count),false);
            }catch(Exception e){booting=false;SetStatus("加载未完成："+e.Message,true);}
        }
        void Save() {if(booting)return;try{store.Save(preferences);}catch(Exception e){SetStatus("收藏未保存："+e.Message,true);}}
        void ResizeCards(){foreach(Control c in films.Controls)c.Width=Math.Max(360,films.ClientSize.Width-22);}
        void RenderFilms() {
            if(catalog==null)return;
            var found=catalog.Families.Where(f=>f.Matches(search.Text));
            if(favoritesOnly.Checked)found=found.Where(f=>preferences.Favorites.Contains(f.Id));
            if(category.SelectedIndex==0)found=found.Where(f=>bindings.Any(b=>b.FamilyId==f.Id));
            if(category.SelectedIndex==2)found=found.Where(f=>!f.Grain&&!f.Rendered);
            if(category.SelectedIndex==3)found=found.Where(f=>f.Grain);
            if(category.SelectedIndex==4)found=found.Where(f=>f.Rendered);
            var all=found.ToList();counts.Text=String.Format("{0} 胶片组 · {1} 份样式  /  当前 {2} 组",catalog.Families.Count,catalog.FileCount,all.Count);
            films.SuspendLayout();foreach(Control control in films.Controls.Cast<Control>().ToArray())control.Dispose();films.Controls.Clear();
            foreach(var family in all.Take(60))films.Controls.Add(FilmCard(family));
            if(all.Count==0)films.Controls.Add(new Label {Text="没有匹配的胶片。\n试试取消“只看收藏”，或切到“全部类型”。",Width=380,Height=70,ForeColor=mute,Padding=new Padding(6,16,0,0)});
            if(all.Count>60)films.Controls.Add(new Label {Text="先显示 60 组；输入胶片名称可继续缩小范围。",Width=380,Height=40,ForeColor=mute});
            ResizeCards();films.ResumeLayout();
        }
        Control FilmCard(FilmFamily family) {
            var panel=new Panel {Height=128,Width=412,BackColor=card,Margin=new Padding(0,0,0,10),Padding=new Padding(12),AccessibleName=family.Name};
            var name=new Label {Text=family.Name,Top=12,Left=13,Width=330,Height=26,Font=new Font("Segoe UI",12F,FontStyle.Bold),ForeColor=ink,AutoEllipsis=true,Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};panel.Controls.Add(name);
            var star=MakeButton(preferences.Favorites.Contains(family.Id)?"★":"☆",34);star.Name="Favorite_"+family.Id;star.AccessibleName="收藏 "+family.Name;star.Height=30;star.Top=9;star.Left=panel.Width-47;star.Anchor=AnchorStyles.Right|AnchorStyles.Top;star.ForeColor=accent;panel.Controls.Add(star);
            star.Click+=(s,e)=>{if(preferences.Favorites.Contains(family.Id))preferences.Favorites.Remove(family.Id);else preferences.Favorites.Add(family.Id);Save();RenderFilms();};
            var description=new Label {Text=family.Category+" · "+(family.Grain?"颗粒":"标准")+" · "+(family.Rendered?"JPG / TIFF":"RAW"),Left=13,Top=42,Width=385,Height=20,ForeColor=mute,Font=new Font("Microsoft YaHei UI",8F),AutoEllipsis=true};panel.Controls.Add(description);
            var choices=new FlowLayoutPanel {Left=12,Top=73,Height=40,Width=385,WrapContents=false,Anchor=AnchorStyles.Left|AnchorStyles.Top|AnchorStyles.Right};panel.Controls.Add(choices);
            foreach(int level in new[]{25,50,75,100}) {
                FilmStyle style=family.At(level);var button=MakeButton(level+"%",80);button.Name="Strength_"+family.Id+"_"+level;button.AccessibleName=family.Name+" "+level+"%";
                button.Tag=style;
                button.Enabled=style!=null&&style.IccExists;button.ForeColor=bindings.Any(b=>b.Style.Uuid==(style==null?null:style.Uuid))?accent:mute;
                if(style!=null&&style.Uuid==confirmedStyleUuid)button.BackColor=Color.FromArgb(83,66,37);
                var tip=new ToolTip();tip.SetToolTip(button,style==null?"厂商未提供此档位":!style.IccExists?"ICC 文件不可访问":style.Name+"\n"+style.Icc);
                button.Disposed+=(s,e)=>tip.Dispose();choices.Controls.Add(button);
                if(style!=null)button.Click+=async(s,e)=>await ApplyStyle(family,style);
            }
            return panel;
        }
        void SetStatus(string text,bool warning){if(status==null||IsDisposed||Disposing)return;status.Text=text;status.ForeColor=warning?Color.FromArgb(243,165,130):mute;}
        void SetBusy(bool value) {
            busy=value;
            if(IsDisposed||Disposing)return;
            UseWaitCursor=value;films.Enabled=!value;search.Enabled=!value;category.Enabled=!value;favoritesOnly.Enabled=!value;
            connectButton.Enabled=!value;lockButton.Enabled=!value;helpButton.Enabled=!value;
            clearButton.Enabled=!value&&live.Checked;
        }
        void UpdateHighlights() {
            if(films==null||IsDisposed||Disposing)return;
            foreach(Control film in films.Controls)foreach(Control group in film.Controls)foreach(Control child in group.Controls) {
                var button=child as Button;var style=button==null?null:button.Tag as FilmStyle;
                if(style!=null)button.BackColor=style.Uuid==confirmedStyleUuid?Color.FromArgb(83,66,37):card;
            }
        }
        void Record(string action,FilmStyle style,string message) {
            Directory.CreateDirectory(store.DirectoryPath);
            bool batchPhase=action=="phase"&&armedTarget!=null&&armedTarget.SelectedCount>1;
            var record=new {at=DateTimeOffset.Now.ToString("o"),version="0.6",action=action,
                catalog=armedTarget==null?null:armedTarget.DocumentPath,variantId=batchPhase?(int?)null:armedId,
                variantUuid=batchPhase||armedTarget==null?null:armedTarget.VariantUuid,
                targetScope=batchPhase?"batch-phase-current-target-in-native-timing-log":"record-target",
                style=style==null?null:style.Name,styleUuid=style==null?null:style.Uuid,message=message};
            File.AppendAllText(Path.Combine(store.DirectoryPath,"style-actions.jsonl"),new JavaScriptSerializer().Serialize(record)+Environment.NewLine);
        }
        void RecordBatchItem(string operation,FilmStyle style,int done,int total,TargetSnapshot item,bool sent) {
            armedTarget=item;armedId=item.VariantId;
            target.Text="本次 "+done+"/"+total+" · "+item.FileName;
            Record("batch-"+operation+"-item-confirmed",style,"confirmed="+done+"; total="+total+"; sent="+sent);
            SetStatus("C1 已确认 "+done+"/"+total+" 张，可取消“启用连接”停止。",false);
        }
        async Task ConnectCatalog() {
            if(busy)return;
            live.Checked=false;live.Enabled=false;setupAcknowledged=false;connectedCatalog=null;
            SetBusy(true);SetStatus("正在读取当前图库与选中照片…",false);
            long permit=attemptGate.Capture();
            try {
                var settings=CaptureSettings.Read(appRoot);string reason;
                if(!settings.ReplaceStyles)throw new InvalidOperationException("C1 尚未启用“替换样式”模式；请先检查原生样式设置。面板不会自动改写设置。");
                if(settings.AutoSyncMetadata!="None")throw new InvalidOperationException("C1 的元数据自动同步不是 None，请先关闭自动同步再连接。");
                if(bindings.Count==0)throw new InvalidOperationException("没有可用 RNI 样式入口，请先检查样式和 ICC 安装。");
                var observed=await Task.Run(()=>CaptureOneBridge.InspectSelection(appRoot));
                if(IsDisposed||Disposing||!attemptGate.IsCurrent(permit))return;
                File.AppendAllText(Path.Combine(store.DirectoryPath,"connection-diagnostics.log"),DateTimeOffset.Now.ToString("o")+" "+observed.Diagnostics+"; count="+observed.SelectedCount+"; path="+observed.DocumentPath+Environment.NewLine);
                if(observed.Modal||!observed.Enabled)throw new InvalidOperationException("C1 窗口当前被识别为弹窗状态；详细原因已记录，未发送。");
                if(!SafetyPolicy.IsAllowedCatalog(observed.DocumentPath))throw new InvalidOperationException("目前只支持 Photography-Master 工作主库与 RNI-Panel-Sandbox，不连接旧目录或会话。");
                reason=SafetyPolicy.CheckDocument(observed,observed);if(reason!=null)throw new InvalidOperationException(reason);
                string kind=String.Equals(observed.DocumentPath,SafetyPolicy.MainCatalogPath,StringComparison.OrdinalIgnoreCase)?"Photography-Master 正式工作主库":"RNI-Panel-Sandbox 测试沙盒";
                string message="将连接："+kind+"\n\n"+observed.DocumentPath+"\n\n每次点击档位或清除，会修改 C1 当时选中照片的调整。\n多选时先核对完整选区，再逐张执行并确认。\n连接本身不会改变照片或查看器。\n同一图库可换照片，无需重连；切换图库会停止。\n不改原片，不在线写图库数据库。\n\n当前已选："+observed.SelectedCount+" 张\n\n确认启用此图库？";
                if(MessageBox.Show(this,message,"连接当前图库",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes){SetStatus("未连接，保持预览模式。",false);return;}
                armedId=observed.VariantId;armedTarget=observed;connectedCatalog=observed;setupAcknowledged=true;confirmedStyleUuid=null;UpdateHighlights();
                target.Text="已连接 "+observed.Title+" · 当前 "+observed.SelectedCount+" 张";live.Enabled=true;live.Checked=true;
                SetStatus("在 C1 选一张或多张，再点档位。逐张原生确认；结果未知不重试。",false);
                Record("connected",null,"user confirmed full catalog path; selected="+observed.SelectedCount+"; each request locks current selection");
            }catch(Exception e){if(!IsDisposed&&!Disposing){live.Checked=false;live.Enabled=false;setupAcknowledged=false;SetStatus(e.Message,true);}}finally{SetBusy(false);}
        }
        async Task ApplyStyle(FilmFamily family,FilmStyle style) {
            if(busy)return;
            var binding=bindings.FirstOrDefault(b=>b.Style.Uuid==style.Uuid);
            if(!live.Checked){SetStatus("预览请求："+style.Name+"。未发送；点击“连接当前图库”后可应用。",false);return;}
            long permit=attemptGate.Capture();
            Func<bool> isCurrent=()=>!IsDisposed&&!Disposing&&live.Checked&&attemptGate.IsCurrent(permit);
            if(binding==null){SetStatus("此样式或对应 ICC 不可访问，请重新加载面板后检查本机安装。",true);return;}
            SetBusy(true);SetStatus("正在核对 "+style.Name+"… 可取消“启用连接”停止。",false);
            TargetSnapshot preparedTarget=null;
            try {
                var settings=CaptureSettings.Read(appRoot);string reason;
                if(!setupAcknowledged||!settings.ReplaceStyles||settings.AutoSyncMetadata!="None")throw new InvalidOperationException("连接条件已改变，请重新检查替换样式模式与元数据同步。");
                var observed=await CaptureOneBridge.PreparePrimaryViewer(appRoot,connectedCatalog,isCurrent);
                preparedTarget=observed;
                if(!isCurrent())throw new OperationCanceledException("连接已取消，未发送。");
                if(!SafetyPolicy.IsAllowedCatalog(observed.DocumentPath))throw new InvalidOperationException("当前不是支持的图库，未发送。");
                reason=SafetyPolicy.CheckDocument(observed,connectedCatalog);if(reason!=null)throw new InvalidOperationException(reason);
                reason=SafetyPolicy.CheckPrimary(observed,observed);if(reason!=null)throw new InvalidOperationException(reason);
                armedTarget=observed;armedId=observed.VariantId;confirmedStyleUuid=null;UpdateHighlights();
                target.Text=observed.SelectedCount==1?"本次："+observed.FileName+" · #"+armedId:"本次选中 "+observed.SelectedCount+" 张 · 主图 "+observed.FileName;
                Record("requested",style,"native verification pending");
                Action<string> progress=message=>{SetStatus(message,false);Record("phase",style,message);};
                if(observed.SelectedCount>1) {
                    var batch=await CaptureOneBridge.ApplySelection(binding,observed,appRoot,isCurrent,bindings,progress,
                        (done,total,item,sent)=>RecordBatchItem("apply",style,done,total,item,sent));
                    if(!isCurrent())throw new OperationCanceledException("连接已取消，后续状态请以 C1 为准。");
                    armedTarget=observed;armedId=observed.VariantId;confirmedStyleUuid=style.Uuid;UpdateHighlights();
                    Record("batch-native-confirmed",style,"confirmed="+batch.Confirmed+"; sent="+batch.Sent+"; total="+batch.Total);
                    SetStatus("C1 已逐张确认 "+batch.Confirmed+" 张：应用 "+batch.Sent+" 张，已有目标样式 "+(batch.Confirmed-batch.Sent)+" 张。",false);
                    return;
                }
                var result=await CaptureOneBridge.Apply(binding,observed,observed,appRoot,isCurrent,bindings,progress);
                if(!isCurrent())throw new OperationCanceledException("连接已取消，后续状态请以 C1 为准。");
                confirmedStyleUuid=style.Uuid;UpdateHighlights();
                Record(result.Sent?"native-confirmed":"already-current-no-send",style,result.ConfirmedStyle);
                SetStatus(result.Sent?"C1 已确认 "+style.Name+" → "+observed.FileName:"当前已是 "+style.Name+"；未重发，效果保持。",false);
            }catch(Exception e){
                try{Record("stopped",style,e.ToString());}catch{}
                if(!IsDisposed&&!Disposing){live.Checked=false;live.Enabled=false;setupAcknowledged=false;SetStatus("连接已暂停："+e.Message+
                    (preparedTarget!=null&&preparedTarget.RestoreMultiViewer?" 查看器可能保持仅主图；未自动重试。":""),true);}
            }finally{SetBusy(false);}
        }
        async Task ClearStyle() {
            if(busy)return;
            if(!live.Checked){SetStatus("当前是预览模式；请先连接图库。未清除。",false);return;}
            long permit=attemptGate.Capture();
            Func<bool> isCurrent=()=>!IsDisposed&&!Disposing&&live.Checked&&attemptGate.IsCurrent(permit);
            SetBusy(true);SetStatus("正在读取当前 RNI 样式，准备清除…",false);
            TargetSnapshot preparedTarget=null;
            try {
                var settings=CaptureSettings.Read(appRoot);string reason;
                if(!setupAcknowledged||settings.AutoSyncMetadata!="None")
                    throw new InvalidOperationException("连接条件已改变，请重新连接图库。未清除。");
                var observed=await CaptureOneBridge.PreparePrimaryViewer(appRoot,connectedCatalog,isCurrent);
                preparedTarget=observed;
                if(!isCurrent())throw new OperationCanceledException("连接已取消，未清除。");
                if(!SafetyPolicy.IsAllowedCatalog(observed.DocumentPath))throw new InvalidOperationException("当前不是支持的图库，未清除。");
                reason=SafetyPolicy.CheckDocument(observed,connectedCatalog);if(reason!=null)throw new InvalidOperationException(reason);
                reason=SafetyPolicy.CheckPrimary(observed,observed);if(reason!=null)throw new InvalidOperationException(reason);
                armedTarget=observed;armedId=observed.VariantId;
                target.Text=observed.SelectedCount==1?"本次："+observed.FileName+" · #"+armedId:"本次选中 "+observed.SelectedCount+" 张 · 主图 "+observed.FileName;
                Record("clear-requested",null,"remove only the currently applied supported RNI style");
                Action<string> progress=message=>{SetStatus(message,false);Record("phase",null,message);};
                if(observed.SelectedCount>1) {
                    var batch=await CaptureOneBridge.ClearSelection(observed,appRoot,isCurrent,bindings,progress,
                        (done,total,item,sent)=>RecordBatchItem("clear",null,done,total,item,sent));
                    if(!isCurrent())throw new OperationCanceledException("连接已取消，结果请以 C1 为准。");
                    armedTarget=observed;armedId=observed.VariantId;confirmedStyleUuid=null;UpdateHighlights();
                    Record("batch-clear-native-confirmed",null,"confirmed="+batch.Confirmed+"; sent="+batch.Sent+"; total="+batch.Total);
                    SetStatus("C1 已逐张确认 "+batch.Confirmed+" 张：清除 RNI "+batch.Sent+" 张，无 RNI "+(batch.Confirmed-batch.Sent)+" 张。",false);
                    return;
                }
                var result=await CaptureOneBridge.Clear(observed,observed,appRoot,isCurrent,bindings,progress);
                if(!isCurrent())throw new OperationCanceledException("连接已取消，结果请以 C1 为准。");
                confirmedStyleUuid=null;UpdateHighlights();
                Record(result.Sent?"clear-native-confirmed":"clear-no-rni",null,result.RemovedStyle??"no identified RNI style");
                SetStatus(result.Sent?"C1 已确认清除 "+result.RemovedStyle+" → "+observed.FileName:"当前没有已识别的 RNI 样式，无需清除。",false);
            }catch(Exception e){
                try{Record("clear-stopped",null,e.ToString());}catch{}
                if(!IsDisposed&&!Disposing){live.Checked=false;live.Enabled=false;setupAcknowledged=false;SetStatus("连接已暂停："+e.Message+
                    (preparedTarget!=null&&preparedTarget.RestoreMultiViewer?" 查看器可能保持仅主图；未自动重试。":""),true);}
            }finally{SetBusy(false);}
        }
        async void ShowSetup() {
            string current="未读取";bool mode=false;string details="";
            try{var settings=CaptureSettings.Read(appRoot);current=settings.ShortcutName;mode=settings.ReplaceStyles;}catch(Exception e){details=e.Message;}
            string why;bool valid=Shortcuts.Verify(Shortcuts.InstalledPath,bindings,out why);
            SetBusy(true);SetStatus("正在只读诊断 C1 当前控件；不会应用或清除样式…",false);
            try {
                var observed=await Task.Run(()=>CaptureOneBridge.Inspect(appRoot));
                string diagnostic=await CaptureOneBridge.DiagnoseStyles(observed);
                Directory.CreateDirectory(store.DirectoryPath);
                File.AppendAllText(Path.Combine(store.DirectoryPath,"native-diagnostics.log"),DateTimeOffset.Now.ToString("o")+Environment.NewLine+observed.Diagnostics+Environment.NewLine+diagnostic+Environment.NewLine);
                details+="\n控件诊断已记录到 native-diagnostics.log。";
            }catch(Exception e){details+="\n控件诊断："+e.Message;}finally{SetBusy(false);}
            if(IsDisposed||Disposing)return;
            string text="RNI Palette — 连接检查\n\n当前键集："+current+"\n替换样式："+(mode?"已配置":"未配置")+"\n样式入口："+(valid?why:"待检查："+why)+"\n\n不限八条快捷键；其余样式按完整原生树路径定位。\n当前键集不是 RNI Panel Demo 时也使用原生树。\n点击前读取原生状态；标准/颗粒同名样式按身份区分。\n多选逐张确认，不向整组选区盲发切换命令。\n只清除当前 RNI，不执行整图重置。\n控件隐藏、读取失败与真正空列表会分别报告。\n\n这是独立 Windows UI 自动化面板，并非官方样式 API。\n仍需 C1 的样式工具与图库身份控件可访问。\n\n收藏和本地日志："+store.DirectoryPath+"\n"+details;
            MessageBox.Show(this,text,"连接设置",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        void ShowHelp() {
            MessageBox.Show(this,"RNI Palette 0.6 — 快速套胶片 / 清除 RNI\n\n• 在 C1 选照片，连接时确认完整图库路径。\n• 在面板搜索胶片、收藏，点击25/50/75/100档位。\n• 全部已安装且 ICC 可用的样式均有原生树入口，不限 Portra。\n• 同一图库换照片不用重连；同档原生确认后不重复发送。\n• 多选先核对整组选区，再临时仅编辑主图、逐张应用/清除；成功后恢复原主图及编辑模式。\n• 批量中断会保留当前主图供检查，并报告已确认数量；不自动重试。\n• 清除仅移除已识别 RNI，保留其他样式/预设，不重置整图。\n• 应用不覆盖未知或混合样式；请先在 C1 核对。\n• 面板不主动跳工具页；原生树展开可能滚动样式工具。控件不可读取时停止，不假装空列表。\n• 这是 UI Automation＋原生操作的独立面板，不是官方样式业务 API；只读数据库核对照片身份。\n• 不改原片，不在线写库。自动连接仍需后续完善。\n\n收藏和日志："+store.DirectoryPath+"\n取消“启用连接”、按 Esc 或关闭面板可停止后续动作。","使用说明",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
    }
}
