using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Xml.Linq;

namespace RniPanel {
    public sealed class CaptureSettings {
        public string Path;
        public string ShortcutName;
        public bool ReplaceStyles;
        public string AutoSyncMetadata;
        public static CaptureSettings Read(string appRoot) {
            string version = FileVersionInfo.GetVersionInfo(System.IO.Path.Combine(appRoot,"CaptureOne.exe")).FileVersion;
            string parent = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Capture_One");
            var candidates = Directory.GetFiles(parent,"user.config",SearchOption.AllDirectories)
                .Where(p => new DirectoryInfo(System.IO.Path.GetDirectoryName(p)).Name == version).ToArray();
            if (candidates.Length != 1) throw new InvalidDataException("不能唯一识别当前 C1 版本的设置文件。");
            var xml = Catalog.ReadXml(candidates[0]);
            Func<string,string> get = key => {
                var entries = xml.Descendants("setting").Where(e=>(string)e.Attribute("name")==key).ToArray();
                if (entries.Length>1) throw new InvalidDataException("同名 C1 设置不唯一。");
                return entries.Length==0 ? null : (string)entries[0].Element("value");
            };
            bool replace;
            return new CaptureSettings {Path=candidates[0],ShortcutName=get("CommandsShortcutsName") ?? "CaptureOne Default",
                ReplaceStyles=Boolean.TryParse(get("StylesReplaceStackWhenStyleClickedDictionary"),out replace) && replace,
                AutoSyncMetadata=get("AutoSyncMetadata")};
        }
    }
    public static partial class CaptureOneBridge {
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
        [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint Type; public INPUTUNION Data; }
        [StructLayout(LayoutKind.Explicit)] struct INPUTUNION {
            [FieldOffset(0)] public KEYBDINPUT Keyboard;
            [FieldOffset(0)] public MOUSEINPUT Mouse;
        }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT {public ushort Vk;public ushort Scan;public uint Flags;public uint Time;public UIntPtr ExtraInfo;}
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT {public int Dx;public int Dy;public uint MouseData;public uint Flags;public uint Time;public UIntPtr ExtraInfo;}
        static string GetDocumentPath(AutomationElement selector,string title) {
            var paths=new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(selector==null)throw new InvalidOperationException("请先将 C1 切回“图库”页；未找到目录选择器，不能确认实际文档路径。");
            foreach(AutomationElement menu in selector.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Menu))) {
                foreach(string value in new[]{menu.Current.HelpText,menu.Current.Name}) {
                    if(String.IsNullOrWhiteSpace(value)||!System.IO.Path.IsPathRooted(value))continue;
                    string extension=System.IO.Path.GetExtension(value);
                    if(extension.Equals(".cocatalogdb",StringComparison.OrdinalIgnoreCase)||extension.Equals(".cosessiondb",StringComparison.OrdinalIgnoreCase))paths.Add(System.IO.Path.GetFullPath(value));
                }
            }
            if(paths.Count!=1)throw new InvalidOperationException("C1 当前目录的完整路径不能唯一确认，拒绝发送。");
            string path=paths.Single();
            if(!File.Exists(path)||!String.Equals(System.IO.Path.GetFileNameWithoutExtension(path),title,StringComparison.Ordinal))
                throw new InvalidOperationException("窗口标题与当前完整文档路径不一致。");
            return path;
        }
        static CacheRequest ElementProperties() {
            var cache=new CacheRequest {TreeScope=TreeScope.Element,AutomationElementMode=AutomationElementMode.Full};
            cache.Add(AutomationElement.AutomationIdProperty);cache.Add(AutomationElement.NameProperty);
            cache.Add(AutomationElement.IsEnabledProperty);cache.Add(AutomationElement.IsOffscreenProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);cache.Add(AutomationElement.ControlTypeProperty);
            return cache;
        }
        static AutomationElement[] FindNamedControls(AutomationElement root,params string[] ids) {
            var filters=ids.Select(id=>(Condition)new PropertyCondition(AutomationElement.AutomationIdProperty,id)).ToArray();
            using(ElementProperties().Activate())
                return root.FindAll(TreeScope.Descendants,filters.Length==1?filters[0]:new OrCondition(filters)).Cast<AutomationElement>().ToArray();
        }
        sealed class TargetControlReferences {
            public readonly IntPtr Handle;
            public readonly long ProcessStart;
            readonly AutomationElement root;
            readonly StructureChangedEventHandler changed;
            AutomationElement[] controls;
            int revision,observedRevision=-1;
            public TargetControlReferences(TargetSnapshot target) {
                Handle=target.Handle;ProcessStart=target.ProcessStartTicks;root=AutomationElement.FromHandle(Handle);
                changed=(sender,args)=>Interlocked.Increment(ref revision);
                Automation.AddStructureChangedEventHandler(root,TreeScope.Subtree,changed);
            }
            public void Release() {
                try{Automation.RemoveStructureChangedEventHandler(root,changed);}catch(ElementNotAvailableException){}
            }
            public AutomationElement[] Read(out bool reused) {
                reused=false;
                for(int attempt=0;attempt<2;attempt++)try {
                    int start=Volatile.Read(ref revision);
                    reused=controls!=null&&observedRevision==start;
                    if(!reused)controls=FindNamedControls(root,"DocumentSelector","MainItemsControl","SummaryText","tb_VariantTitle");
                    // Cache only element references. Every inspection gets fresh
                    // text, visibility and bounds; no old photo/path is reused.
                    var current=controls.Select(e=>e.GetUpdatedCache(ElementProperties())).ToArray();
                    if(Volatile.Read(ref revision)!=start) {
                        controls=null;
                        // A native tooltip/menu can legitimately change the
                        // root during observation. Rediscover once, never return
                        // values captured across that structural change.
                        continue;
                    }
                    foreach(string id in new[]{"DocumentSelector","MainItemsControl","SummaryText","tb_VariantTitle"})
                        if(!current.Any(e=>e.Cached.AutomationId==id&&!e.Cached.IsOffscreen)) {
                            controls=null;
                            throw new InvalidOperationException("C1 目标控件已隐藏或布局改变（"+id+"），不能使用旧选区。");
                        }
                    observedRevision=start;
                    return current;
                } catch(ElementNotAvailableException) {
                    controls=null;
                }
                throw new InvalidOperationException("C1 目标界面仍在变化，无法稳定确认当前选区，已停止后续动作。");
            }
        }
        static readonly object targetControlsLock=new object();
        static TargetControlReferences targetControls;
        static AutomationElement[] ReadTargetControls(TargetSnapshot target,out bool reused) {
            lock(targetControlsLock) {
                if(targetControls==null||targetControls.Handle!=target.Handle||targetControls.ProcessStart!=target.ProcessStartTicks) {
                    if(targetControls!=null)targetControls.Release();
                    targetControls=new TargetControlReferences(target);
                }
                return targetControls.Read(out reused);
            }
        }
        public static TargetSnapshot Inspect(string appRoot) {return InspectCore(appRoot,false);}
        public static TargetSnapshot InspectPrimary(string appRoot) {return InspectCore(appRoot,true);}
        static TargetSnapshot InspectCore(string appRoot,bool allowMultiple) {
            string executable = System.IO.Path.GetFullPath(System.IO.Path.Combine(appRoot,"CaptureOne.exe"));
            var processes = Process.GetProcessesByName("CaptureOne").Where(p=> {
                try {return p.MainWindowHandle!=IntPtr.Zero && p.MainModule.FileName.Equals(executable,StringComparison.OrdinalIgnoreCase);}catch{return false;}
            }).ToArray();
            if(processes.Length!=1) throw new InvalidOperationException("请只打开一个 Capture One 主窗口。");
            var process=processes[0];
            IntPtr handle=process.MainWindowHandle;
            IntPtr popup=GetLastActivePopup(handle);
            var state=new TargetSnapshot {Handle=handle,ProcessId=process.Id,ProcessStartTicks=process.StartTime.ToUniversalTime().Ticks,
                Title=process.MainWindowTitle,Enabled=IsWindowEnabled(handle),Modal=popup!=handle && IsWindowVisible(popup)};
            state.Diagnostics="Enabled="+state.Enabled+"; popup="+popup+"; main="+handle;
            if(!state.Enabled || state.Modal) return state;
            bool reusedControls;
            var controls=ReadTargetControls(state,out reusedControls);
            state.Diagnostics+="; targetControls="+(reusedControls?"references-refreshed":"tree-discovered");
            Func<string,AutomationElement> control=id=>controls.FirstOrDefault(e=>e.Cached.AutomationId==id&&!e.Cached.IsOffscreen);
            state.DocumentPath=GetDocumentPath(control("DocumentSelector"),state.Title);
            var browser=control("MainItemsControl");
            if(browser==null) throw new InvalidOperationException("未找到 C1 照片浏览器；请关闭弹窗并显示浏览器。");
            object selection;
            AutomationElement[] selected;
            if(browser.TryGetCurrentPattern(SelectionPattern.Pattern,out selection)) selected=((SelectionPattern)selection).Current.GetSelection();
            else selected=browser.FindAll(TreeScope.Descendants,new PropertyCondition(SelectionItemPattern.IsSelectedProperty,true)).Cast<AutomationElement>().ToArray();
            var variants=selected.Select(e=>e.Current.Name).Where(n=>Regex.IsMatch(n??"",@"VariantID:\s*\d+")).Distinct().ToArray();
            state.Diagnostics+="; selectionPattern="+(selection!=null)+"; rawSelected="+selected.Length+"; names="+String.Join(" | ",selected.Select(e=>e.Current.Name))+"; browserOffscreen="+browser.Current.IsOffscreen;
            var summary=control("SummaryText");
            if(summary!=null)state.Diagnostics+="; summary="+summary.Cached.Name;
            state.SelectedCount=variants.Length;
            int summaryCount=0;
            bool knownCount=summary!=null&&SelectionSummary.TryCount(summary.Cached.Name,out summaryCount);
            if(knownCount)state.SelectedCount=summaryCount;
            if(knownCount&&summaryCount>=1&&(allowMultiple||summaryCount==1)) {
                var titles=controls.Where(e=>e.Cached.AutomationId=="tb_VariantTitle"&&!e.Cached.IsOffscreen&&!String.IsNullOrWhiteSpace(e.Cached.Name))
                    .Select(e=>e.Cached.Name).ToArray();
                if(titles.Length!=1)throw new InvalidOperationException("请使用单图查看器；不能唯一确定当前照片标题。");
                string filename=titles[0];int index=0;
                var number=Regex.Match(filename,@"^(.*?)\s+\[(\d+)\]$");
                if(number.Success){filename=number.Groups[1].Value;index=int.Parse(number.Groups[2].Value)-1;}
                if(index<0)throw new InvalidOperationException("变体序号无效。");
                var identity=CatalogReader.ResolveSingle(state.DocumentPath,filename);
                string peerName="Variant: "+identity.FileName+" - VariantID: "+identity.Id;
                if(variants.Length>0&&!variants.Contains(peerName,StringComparer.Ordinal))
                    throw new InvalidOperationException("当前选区与查看器不是同一张照片，未发送。");
                var peer=browser.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,peerName));
                if(peer==null)throw new InvalidOperationException("查看器、图库记录与浏览器条目不一致，未发送。");
                state.VariantId=identity.Id;state.VariantUuid=identity.Uuid;state.VariantName=peerName;state.FileName=identity.FileName;
                state.Diagnostics+="; adapter=selection-summary + one-primary-viewer-caption + globally-single-variant-readonly-db + browser-peer; uuid="+identity.Uuid;
            }
            if(state.SelectedCount!=1&&!allowMultiple)state.Diagnostics+="; a single selected image and one viewer are required";
            return state;
        }
        static void RequireActiveWindow(TargetSnapshot expected,Func<bool> isCurrent) {
            if(!isCurrent())throw new OperationCanceledException("连接已取消，未继续操作。");
            if(!IsWindowEnabled(expected.Handle)||GetForegroundWindow()!=expected.Handle)
                throw new InvalidOperationException("C1 焦点已改变或窗口不可操作，已停止。");
            IntPtr popup=GetLastActivePopup(expected.Handle);
            if(popup!=expected.Handle&&IsWindowVisible(popup))throw new InvalidOperationException("C1 出现弹窗，已停止。");
            using(var process=Process.GetProcessById(expected.ProcessId)) {
                if(process.MainWindowHandle!=expected.Handle||process.StartTime.ToUniversalTime().Ticks!=expected.ProcessStartTicks||
                    !String.Equals(process.MainWindowTitle,expected.Title,StringComparison.Ordinal))
                    throw new InvalidOperationException("C1 文档或进程已更换，已停止。");
            }
            foreach(int modifier in new[]{0x10,0x11,0x12})if((GetAsyncKeyState(modifier)&0x8000)!=0)
                throw new InvalidOperationException("请先松开 Shift / Ctrl / Alt，已停止。");
            if((GetAsyncKeyState(0x1b)&0x8000)!=0)throw new OperationCanceledException("已按 Esc，停止后续操作。");
        }
        static bool IsStylesToolName(string name) {
            name=(name??"").Trim();
            return name=="样式与预设"||String.Equals(name,"Styles and Presets",StringComparison.OrdinalIgnoreCase)||
                String.Equals(name,"Styles & Presets",StringComparison.OrdinalIgnoreCase);
        }
        static string NameToken(string name){return "rni-name:"+name;}
        static IEnumerable<string> KnownStyleTokens(IEnumerable<StyleBinding> bindings) {
            return bindings.Select(b=>b.Style.Uuid).Concat(bindings.Select(b=>NameToken(b.Style.Name))).Distinct(StringComparer.Ordinal);
        }
        static void ClickNativeRow(AutomationElement row) {
            var bounds=row.Current.BoundingRectangle;
            if(row.Current.IsOffscreen||bounds.IsEmpty||bounds.Width<2||bounds.Height<2)throw new InvalidOperationException("当前样式行不可点击，未清除。");
            int left=GetSystemMetrics(76),top=GetSystemMetrics(77),width=GetSystemMetrics(78),height=GetSystemMetrics(79);
            if(width<=1||height<=1)throw new InvalidOperationException("无法确定显示器区域，未清除。");
            int x=(int)((bounds.X+bounds.Width/2-left)*65535/(width-1));
            int y=(int)((bounds.Y+bounds.Height/2-top)*65535/(height-1));
            var inputs=new[]{
                new INPUT {Type=0,Data=new INPUTUNION {Mouse=new MOUSEINPUT {Dx=x,Dy=y,Flags=0xc001}}},
                new INPUT {Type=0,Data=new INPUTUNION {Mouse=new MOUSEINPUT {Flags=2}}},
                new INPUT {Type=0,Data=new INPUTUNION {Mouse=new MOUSEINPUT {Flags=4}}}
            };
            if(SendInput((uint)inputs.Length,inputs,Marshal.SizeOf(typeof(INPUT)))!=inputs.Length)
                throw new InvalidOperationException("打开当前样式菜单的输入未完整接收，已停止；不重发。");
        }
        static void RequireStyleMenu(TargetSnapshot expected,Func<bool> current) {
            if(!current())throw new OperationCanceledException("连接已取消，未继续清除。");
            uint process;GetWindowThreadProcessId(GetForegroundWindow(),out process);
            if(process!=expected.ProcessId||!IsWindowEnabled(expected.Handle)||(GetAsyncKeyState(0x1b)&0x8000)!=0)
                throw new InvalidOperationException("样式菜单焦点已改变，已停止清除。");
            using(var owner=Process.GetProcessById(expected.ProcessId))
                if(owner.StartTime.ToUniversalTime().Ticks!=expected.ProcessStartTicks||owner.MainWindowTitle!=expected.Title)
                    throw new InvalidOperationException("样式菜单期间 C1 文档已改变，未继续。");
        }
        static readonly SemaphoreSlim styleReadGate=new SemaphoreSlim(1,1);
        static readonly object timingLogLock=new object();
        static void LogNativeTiming(string value) {
            try {
                lock(timingLogLock) {
                    Directory.CreateDirectory(PreferencesStore.SharedDirectory);
                    File.AppendAllText(System.IO.Path.Combine(PreferencesStore.SharedDirectory,"native-timing.log"),DateTimeOffset.Now.ToString("o")+" "+value+Environment.NewLine);
                }
            }catch(IOException){}catch(UnauthorizedAccessException){}
        }
        static NativeStyleReader styleReader;
        sealed class StylesReadException:InvalidOperationException {
            public StylesReadException(string code,string message):base("["+code+"] "+message){}
        }
        sealed class StyleListReference {
            public AutomationElement List,Tool,Header;
        }
        sealed class NativeStyleReader {
            readonly IntPtr handle;
            readonly long processStart;
            StyleListReference[] candidates;
            readonly Dictionary<string,AutomationElement> styleBoxes=new Dictionary<string,AutomationElement>(StringComparer.OrdinalIgnoreCase);
            public Action OperationGuard=delegate{};
            public Action<string> Stage=delegate{};
            public NativeStyleReader(TargetSnapshot target){handle=target.Handle;processStart=target.ProcessStartTicks;}
            public bool Matches(TargetSnapshot target){return handle==target.Handle&&processStart==target.ProcessStartTicks;}
            static bool HasBounds(AutomationElement element) {
                var bounds=element.Cached.BoundingRectangle;
                return !bounds.IsEmpty&&bounds.Width>0&&bounds.Height>0;
            }
            StyleListReference LocateOwner(AutomationElement list) {
                var found=new StyleListReference {List=list};
                Stage("locate-owner.parent.begin");
                var parent=TreeWalker.ControlViewWalker.GetParent(list);
                for(int depth=0;parent!=null&&depth<16;depth++) {
                    Stage("locate-owner.depth="+depth);
                    var live=parent.GetUpdatedCache(ElementProperties());
                    if(live.Cached.ControlType==ControlType.Window)break;
                    if(live.Cached.AutomationId=="expander"||IsStylesToolName(live.Cached.Name)) {
                        found.Tool=parent;
                        if(IsStylesToolName(live.Cached.Name))return found;
                        Stage("locate-owner.header.begin");
                        using(ElementProperties().Activate())found.Header=parent.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"headerTextBlock"));
                        if(found.Header!=null&&!IsStylesToolName(found.Header.Cached.Name))found.Header=null;
                        Stage("locate-owner.header.end");
                        return found;
                    }
                    parent=TreeWalker.ControlViewWalker.GetParent(parent);
                }
                return found;
            }
            void Discover() {
                // Discovery runs once per live UI tree. All stable observations
                // after this read only the selected tool subtree, never the browser.
                Stage("discover-list.begin");
                AutomationElement list;
                using(ElementProperties().Activate())list=AutomationElement.FromHandle(handle).FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty,"ListViewAppliedStyles"));
                Stage("discover-list.end found="+(list!=null));
                candidates=list==null?new StyleListReference[0]:new[]{LocateOwner(list)};
            }
            StyleListReference VisibleList() {
                Stage("visible-list.begin cached="+(candidates!=null));
                if(candidates==null)Discover();
                if(candidates.Length==0){candidates=null;throw new StylesReadException("styles-control-missing","当前界面未提供已应用样式控件；这不是空样式列表。未发送。");}
                var visible=new List<StyleListReference>();
                bool collapsed=false;
                foreach(var candidate in candidates) {
                    var list=candidate.List.GetUpdatedCache(ElementProperties());
                    Stage("visible-list.properties hidden="+list.Cached.IsOffscreen+" bounds="+HasBounds(list));
                    if(!list.Cached.IsEnabled)continue;
                    if(!list.Cached.IsOffscreen&&HasBounds(list)){visible.Add(candidate);continue;}
                    if(candidate.Tool==null)continue;
                    var tool=candidate.Tool.GetUpdatedCache(ElementProperties());
                    if(tool.Cached.IsOffscreen||!tool.Cached.IsEnabled||!HasBounds(tool))continue;
                    bool named=IsStylesToolName(tool.Cached.Name);
                    if(!named&&candidate.Header!=null) {
                        var header=candidate.Header.GetUpdatedCache(ElementProperties());
                        named=!header.Cached.IsOffscreen&&IsStylesToolName(header.Cached.Name);
                    }
                    if(!named)continue;
                    object pattern;
                    if(candidate.Tool.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out pattern)) {
                        Stage("visible-list.empty-owner.expand-state");
                        if(((ExpandCollapsePattern)pattern).Current.ExpandCollapseState!=ExpandCollapseState.Expanded){collapsed=true;continue;}
                        // WPF collapses the empty ListView's rectangle. The fresh,
                        // named and expanded owning tool is the emptiness evidence.
                        visible.Add(candidate);
                    }
                }
                if(visible.Count==0){candidates=null;styleBoxes.Clear();throw new StylesReadException(collapsed?"styles-tool-collapsed":"styles-control-hidden",
                    collapsed?"样式工具处于折叠状态，无法核实当前样式；未发送。":"已找到样式控件，但目前不可见或不可读；不能当作“未应用样式”。未发送。");}
                if(visible.Count!=1)throw new StylesReadException("styles-control-ambiguous","同时出现多个可读的已应用样式列表，不能唯一确认；未发送。");
                Stage("visible-list.verified");
                return visible[0];
            }
            public string[] Read() {
                try {
                    var selected=VisibleList();
                    Stage("read-list.scroll.begin");
                    object scroll;
                    if(selected.List.TryGetCurrentPattern(ScrollPattern.Pattern,out scroll)&&((ScrollPattern)scroll).Current.VerticallyScrollable)
                        throw new StylesReadException("styles-list-incomplete","已应用列表有未完整显示的滚动项，未发送。");
                    AutomationElement[] rows;
                    Stage("read-list.rows.begin");
                    using(ElementProperties().Activate())rows=selected.List.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem)).Cast<AutomationElement>().ToArray();
                    Stage("read-list.rows.end count="+rows.Length);
                    var result=new List<string>();
                    foreach(var row in rows) {
                        if(row.Cached.IsOffscreen)throw new StylesReadException("styles-row-hidden","已应用样式存在隐藏条目，未发送。");
                        AutomationElement[] text;
                        using(ElementProperties().Activate())text=row.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text)).Cast<AutomationElement>().ToArray();
                        var names=text.Select(e=>e.Cached.Name).Where(n=>!String.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToArray();
                        if(names.Length!=1)throw new StylesReadException("styles-row-unreadable","当前样式行名称不能唯一读取；这是读取失败，不是空列表。未发送。");
                        result.Add(names[0]);
                    }
                    return result.ToArray();
                }catch(ElementNotAvailableException) {
                    // Do not use values from the old tree or silently rediscover in
                    // the same observation. The next user request starts discovery.
                    candidates=null;
                    styleBoxes.Clear();
                    throw new StylesReadException("styles-control-stale","C1 刚重建了样式控件，本次未发送；可以再次点击读取新界面。");
                }catch(StylesReadException){throw;}
                catch(Exception e) {throw new InvalidOperationException("[styles-read-failed] C1 样式控件读取失败，未将其解释为空列表："+e.Message,e);}
            }
            static AutomationElement[] Children(AutomationElement parent,ControlType kind) {
                // Path matching needs labels only; calculating screen rectangles
                // for every offscreen style in a large strength folder is costly.
                var cache=new CacheRequest {TreeScope=TreeScope.Element|TreeScope.Children,TreeFilter=Automation.ControlViewCondition,AutomationElementMode=AutomationElementMode.Full};
                cache.Add(AutomationElement.NameProperty);cache.Add(AutomationElement.ControlTypeProperty);
                using(cache.Activate())return parent.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,kind)).Cast<AutomationElement>().ToArray();
            }
            static string ItemLabel(AutomationElement item) {
                // C1 TreeItem.Name is its ViewModel type, not the folder label.
                // Only immediate header children belong to this item; descendant
                // text would accidentally include labels from expanded subfolders.
                var headers=item.CachedChildren.Cast<AutomationElement>().ToArray();
                var labels=headers.Where(e=>e.Cached.ControlType==ControlType.Text).Select(e=>e.Cached.Name).Where(n=>!String.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToArray();
                if(labels.Length==1)return labels[0];
                var boxes=headers.Where(e=>e.Cached.ControlType==ControlType.CheckBox).Select(e=>e.Cached.Name).Where(n=>!String.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToArray();
                return boxes.Length==1?boxes[0]:null;
            }
            static void Expand(AutomationElement item,Action validate) {
                object pattern;
                if(!item.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out pattern))return;
                var expand=(ExpandCollapsePattern)pattern;
                if(expand.Current.ExpandCollapseState==ExpandCollapseState.Collapsed) {
                    validate();expand.Expand();validate();
                }
            }
            AutomationElement[] FindStyleTrees(AutomationElement owner) {
                // The four Trees are siblings in the tool body/scroll pane. Never
                // enumerate descendants of a Tree/TreeItem just to find its sibling.
                var result=new List<AutomationElement>();
                var queue=new Queue<Tuple<AutomationElement,int>>();queue.Enqueue(Tuple.Create(owner,0));
                while(queue.Count>0) {
                    var next=queue.Dequeue();
                    if(next.Item2>4)continue;
                    AutomationElement[] children;
                    using(ElementProperties().Activate())children=next.Item1.FindAll(TreeScope.Children,Condition.TrueCondition).Cast<AutomationElement>().ToArray();
                    foreach(var child in children) {
                        var type=child.Cached.ControlType;
                        if(type==ControlType.Tree)result.Add(child);
                        else if(type==ControlType.Custom||type==ControlType.Pane||type==ControlType.Group)queue.Enqueue(Tuple.Create(child,next.Item2+1));
                    }
                }
                return result.ToArray();
            }
            AutomationElement StyleBox(StyleBinding binding,Action validate) {
                Stage("prepare-style.begin "+(binding==null||binding.Style==null?"?":binding.Style.Name));
                if(binding==null||binding.Style==null||binding.NativePath==null||binding.NativePath.Length<2)
                    throw new StylesReadException("style-path-missing","样式没有完整原生路径，未发送。");
                AutomationElement cached;
                if(styleBoxes.TryGetValue(binding.Style.Uuid,out cached)) {
                    try {if(cached.Current.Name==binding.Style.Name&&cached.Current.IsEnabled){Stage("prepare-style.cached");return cached;}}
                    catch(ElementNotAvailableException){}
                    styleBoxes.Remove(binding.Style.Uuid);
                }
                var owner=VisibleList().Tool;
                if(owner==null)throw new StylesReadException("styles-owner-unavailable","无法定位当前样式工具，未发送。");
                Stage("prepare-style.trees.begin");
                var trees=FindStyleTrees(owner);
                Stage("prepare-style.trees.end count="+trees.Length);
                var roots=new List<AutomationElement>();
                bool user=binding.Style.Path.StartsWith(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CaptureOne")+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
                foreach(var tree in trees)foreach(var wrapper in Children(tree,ControlType.TreeItem)) {
                    string label=ItemLabel(wrapper);
                    bool matches=user?(label=="用户样式"||String.Equals(label,"User Styles",StringComparison.OrdinalIgnoreCase)):
                        (label=="内置样式"||String.Equals(label,"Built-in Styles",StringComparison.OrdinalIgnoreCase)||String.Equals(label,"Built In Styles",StringComparison.OrdinalIgnoreCase));
                    if(!matches)continue;
                    Stage("prepare-style.wrapper "+label);
                    Expand(wrapper,validate);
                    roots.AddRange(Children(wrapper,ControlType.TreeItem).Where(e=>String.Equals(ItemLabel(e),binding.NativePath[0],StringComparison.Ordinal)));
                }
                if(roots.Count!=1)throw new StylesReadException("style-root-ambiguous","不能唯一定位原生样式目录："+binding.NativePath[0]+"。未发送。");
                var item=roots[0];
                for(int i=1;i<binding.NativePath.Length;i++) {
                    Stage("prepare-style.path.expand "+binding.NativePath[i-1]);
                    validate();Expand(item,validate);
                    Stage("prepare-style.path.children "+binding.NativePath[i]);
                    var matches=Children(item,ControlType.TreeItem).Where(e=>String.Equals(ItemLabel(e),binding.NativePath[i],StringComparison.Ordinal)).ToArray();
                    Stage("prepare-style.path.matched "+binding.NativePath[i]+" count="+matches.Length);
                    if(matches.Length!=1)throw new StylesReadException("style-path-unavailable","原生样式路径不能唯一定位："+String.Join(" / ",binding.NativePath.Take(i+1))+"。未发送。");
                    item=matches[0];
                }
                Stage("prepare-style.checkbox.begin");
                var boxes=item.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"StyleCheckBox")).Cast<AutomationElement>()
                    .Where(e=>e.Current.Name==binding.Style.Name).ToArray();
                if(boxes.Length!=1)throw new StylesReadException("style-checkbox-unavailable","原生样式没有唯一可操作的勾选控件："+binding.Style.Name+"。未发送。");
                styleBoxes[binding.Style.Uuid]=boxes[0];Stage("prepare-style.ready");return boxes[0];
            }
            public ToggleState StyleState(StyleBinding binding,Action validate) {
                validate();var box=StyleBox(binding,validate);object toggle;
                Stage("style-state.pattern.begin");
                if(!box.TryGetCurrentPattern(TogglePattern.Pattern,out toggle))throw new StylesReadException("style-toggle-unavailable","原生样式未提供勾选状态，无法区分同名标准/颗粒版本。未发送。");
                var state=((TogglePattern)toggle).Current.ToggleState;Stage("style-state.end "+state);return state;
            }
            public string[] ReadIdentities(StyleBinding[] bindings,Action validate) {
                Action guarded=()=>{OperationGuard();validate();};
                var names=Read();var identities=new List<string>();
                foreach(string name in names) {
                    var matches=bindings.Where(b=>b.Style.Name==name).ToArray();
                    if(matches.Length==0){identities.Add("unmanaged:"+name);continue;}
                    // Do not expand every same-name candidate to discover its
                    // identity: freshly realized C1 leaves start unchecked anyway.
                    // Only already-realized, live checkboxes can contribute an On
                    // observation; otherwise use the exact applied-row fallback.
                    var checkedMatches=new List<StyleBinding>();
                    foreach(var match in matches) {
                        AutomationElement box;object toggle;
                        if(!styleBoxes.TryGetValue(match.Style.Uuid,out box))continue;
                        try {
                            guarded();
                            if(box.Current.Name==name&&box.TryGetCurrentPattern(TogglePattern.Pattern,out toggle)&&((TogglePattern)toggle).Current.ToggleState==ToggleState.On)
                                checkedMatches.Add(match);
                        }catch(ElementNotAvailableException){styleBoxes.Remove(match.Style.Uuid);}
                    }
                    // C1 initializes lazily-created tree checkboxes to false and
                    // updates them only on the next CheckmarkChanged event. An
                    // all-Off result is not proof of an empty/current identity.
                    identities.Add(checkedMatches.Count==1?checkedMatches[0].Style.Uuid:NameToken(name));
                }
                return identities.ToArray();
            }
            public void RemoveApplied(string name,TargetSnapshot target,Func<bool> current) {
                OperationGuard();RequireActiveWindow(target,current);
                var selected=VisibleList();
                var matches=new List<AutomationElement>();
                foreach(var row in Children(selected.List,ControlType.ListItem)) {
                    var labels=row.CachedChildren.Cast<AutomationElement>().Where(e=>e.Cached.ControlType==ControlType.Text)
                        .Select(e=>e.Cached.Name).Where(n=>!String.IsNullOrWhiteSpace(n)).ToArray();
                    if(labels.Length==1&&labels[0]==name)matches.Add(row);
                }
                if(matches.Count!=1)throw new StylesReadException("style-row-ambiguous","不能唯一定位待清除的原生样式行，未清除。");
                Stage("remove-row.open-menu");
                OperationGuard();RequireActiveWindow(target,current);ClickNativeRow(matches[0]);
                AutomationElement action=null;
                for(int attempt=0;attempt<5&&action==null;attempt++) {
                    Thread.Sleep(60);OperationGuard();RequireStyleMenu(target,current);
                    var windows=AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ProcessIdProperty,target.ProcessId))
                        .Cast<AutomationElement>().OrderBy(e=>e.Current.NativeWindowHandle==target.Handle.ToInt64()?1:0).ToArray();
                    foreach(var window in windows) {
                        var actions=window.FindAll(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem),
                            new OrCondition(new PropertyCondition(AutomationElement.NameProperty,"从背景中清除"),new PropertyCondition(AutomationElement.NameProperty,"Clear from Background"))))
                            .Cast<AutomationElement>().Where(e=>!e.Current.IsOffscreen&&e.Current.IsEnabled).ToArray();
                        if(actions.Length>1)throw new StylesReadException("style-remove-menu-ambiguous","清除菜单目标不唯一，未继续。");
                        if(actions.Length==1){action=actions[0];break;}
                    }
                }
                if(action==null)
                    throw new StylesReadException("style-remove-menu-unavailable","未读到当前样式的“从背景中清除”动作；未执行清除。");
                // C1 handles this menu's PreviewMouseDown, not MenuItem.Command;
                // UIA Invoke closes it without executing RemoveStyleCommand.
                // Click its freshly resolved native row once, never retry Invoke.
                Stage("remove-row.click-clear-menu");
                OperationGuard();RequireStyleMenu(target,current);ClickNativeRow(action);
                Stage("remove-row.click-submitted");
            }
            public void ToggleStyle(StyleBinding binding,bool remove,Action validate) {
                Action guarded=()=>{OperationGuard();validate();};
                // Path discovery is completed during read/preflight, before the
                // workflow's final target check. A send never performs slow tree
                // expansion and then acts on an old photo snapshot.
                AutomationElement box;object toggle;
                if(!styleBoxes.TryGetValue(binding.Style.Uuid,out box)||box.Current.Name!=binding.Style.Name||!box.Current.IsEnabled)
                    throw new StylesReadException("style-leaf-stale","已核对的原生样式控件失效，本次未发送；请重新点击。");
                if(!box.TryGetCurrentPattern(TogglePattern.Pattern,out toggle))throw new StylesReadException("style-toggle-unavailable","原生样式不支持勾选操作，未发送。");
                var state=((TogglePattern)toggle).Current.ToggleState;
                if(state==ToggleState.Indeterminate||state!=(remove?ToggleState.On:ToggleState.Off))
                    throw new StylesReadException("style-state-changed","原生样式勾选状态与已核对结果不同，未发送；不会以切换命令重试。");
                guarded();((TogglePattern)toggle).Toggle();
            }
            public string Diagnose() {
                if(candidates==null)Discover();
                var output=new System.Text.StringBuilder();
                output.AppendLine("style lists="+candidates.Length);
                foreach(var item in candidates) {
                    var root=item.Tool??TreeWalker.ControlViewWalker.GetParent(item.List)??item.List;
                    var cache=ElementProperties();cache.TreeScope=TreeScope.Subtree;cache.TreeFilter=Automation.ControlViewCondition;
                    cache.Add(AutomationElement.HelpTextProperty);cache.Add(AutomationElement.ItemStatusProperty);
                    cache.Add(AutomationElement.IsTogglePatternAvailableProperty);cache.Add(AutomationElement.IsExpandCollapsePatternAvailableProperty);
                    cache.Add(AutomationElement.IsInvokePatternAvailableProperty);cache.Add(AutomationElement.IsSelectionItemPatternAvailableProperty);
                    cache.Add(TogglePattern.ToggleStateProperty);cache.Add(ExpandCollapsePattern.ExpandCollapseStateProperty);
                    var snapshot=root.GetUpdatedCache(cache);
                    int remaining=500;
                    Describe(snapshot,output,0,ref remaining);
                }
                return output.ToString();
            }
            static void Describe(AutomationElement element,System.Text.StringBuilder output,int depth,ref int remaining) {
                if(remaining--<=0||depth>14)return;
                var current=element.Cached;
                output.Append(new string(' ',depth*2)).Append(current.ControlType.ProgrammaticName).Append(" id=").Append(current.AutomationId)
                    .Append(" name=").Append(current.Name).Append(" help=").Append(current.HelpText).Append(" status=").Append(current.ItemStatus)
                    .Append(" hidden=").Append(current.IsOffscreen).Append(" toggle=").Append(element.GetCachedPropertyValue(TogglePattern.ToggleStateProperty,true))
                    .Append(" expand=").Append(element.GetCachedPropertyValue(ExpandCollapsePattern.ExpandCollapseStateProperty,true))
                    .Append(" invoke=").Append(element.GetCachedPropertyValue(AutomationElement.IsInvokePatternAvailableProperty)).AppendLine();
                foreach(AutomationElement child in element.CachedChildren) {
                    if(remaining<=0)break;
                    Describe(child,output,depth+1,ref remaining);
                }
            }
        }
        static async Task<T> ReadStylesBounded<T>(TargetSnapshot target,Func<NativeStyleReader,T> read,bool sends=false) {
            // UIA cannot cancel a blocked provider call. Keep a single in-flight
            // worker after timeout, instead of leaving orphan full-tree scans that
            // pile up each time the user retries.
            if(!await styleReadGate.WaitAsync(0))throw new StylesReadException("styles-read-pending","上一次 C1 样式读取仍未返回，本次未启动重复读取或发送。");
            int expired=0;
            var watch=Stopwatch.StartNew();string stage="queued";
            string operation=Guid.NewGuid().ToString("N").Substring(0,8);
            Action<string> trace=value=> {
                Volatile.Write(ref stage,value);
                LogNativeTiming("op="+operation+" elapsedMs="+watch.ElapsedMilliseconds+" expired="+Volatile.Read(ref expired)+" "+value);
            };
            trace("queued sends="+sends);
            var task=Task.Run(()=> {
                try {
                    trace("worker.begin");
                    if(styleReader==null||!styleReader.Matches(target))styleReader=new NativeStyleReader(target);
                    styleReader.Stage=trace;
                    styleReader.OperationGuard=()=>{if(Volatile.Read(ref expired)!=0)throw new OperationCanceledException("本次原生读取/操作已超时，禁止后续展开或发送。");};
                    return read(styleReader);
                }catch(Exception e){trace("worker.fault "+e.GetType().Name+" "+e.Message);throw;}
                finally {trace("worker.finished");styleReadGate.Release();}
            });
            if(await Task.WhenAny(task,Task.Delay(8000))!=task) {
                Interlocked.Exchange(ref expired,1);
                trace("TIMEOUT last-stage="+Volatile.Read(ref stage));
                // Observe a late failure; the worker owns the gate until complete.
                var observer=task.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException((sends?"[styles-send-timeout] 原生样式操作超过8秒，结果未知；已停止，不会重发。":"[styles-read-timeout] C1 样式读取超过8秒；未发送，不会重发。后台只保留这一次读取，避免重复遍历。")+" 阶段记录："+operation+" / "+Volatile.Read(ref stage));
            }
            return await task;
        }
        public static Task<string> DiagnoseStyles(TargetSnapshot target) {
            return ReadStylesBounded(target,reader=>reader.Diagnose());
        }
        sealed class LiveStyleSession:ILiveStyleSession,IClearStyleSession {
            readonly StyleBinding binding;
            readonly StyleBinding[] clearBindings;
            readonly TargetSnapshot expected,armed;
            readonly string appRoot;
            readonly Func<bool> isCurrent;
            readonly Action<string> progress;
            readonly bool primaryOnly;
            bool foregroundPrepared;
            bool commandCompleted;
            bool appliedRequested;
            string pendingRemovalName;
            readonly HashSet<string> cleared=new HashSet<string>(StringComparer.Ordinal);
            string[] lastObservedStyles;
            public bool IsCurrent { get {return isCurrent();} }
            public LiveStyleSession(StyleBinding b,TargetSnapshot e,TargetSnapshot a,string root,Func<bool> current,Action<string> report,
                IEnumerable<StyleBinding> removable=null,bool primaryOnly=false) {
                binding=b;expected=e;armed=a;appRoot=root;isCurrent=current;progress=report;
                clearBindings=(removable??Enumerable.Empty<StyleBinding>()).ToArray();
                this.primaryOnly=primaryOnly;
            }
            public async Task ValidateTarget() {
                try {
                    if(!IsCurrent)throw new OperationCanceledException("连接已取消。");
                    var current=await Task.Run(()=>primaryOnly?InspectPrimary(appRoot):Inspect(appRoot));
                    string reason=primaryOnly?SafetyPolicy.CheckPrimary(current,armed):SafetyPolicy.Check(current,armed);
                    if(reason!=null||!SafetyPolicy.IsAllowedCatalog(current.DocumentPath))
                        throw new InvalidOperationException(reason??"当前不是支持的图库，未发送。");
                    if(foregroundPrepared)RequireActiveWindow(expected,isCurrent);
                    if(primaryOnly)await Task.Run(()=>RequirePrimaryOnly(expected));
                }catch(Exception e) {
                    if(commandCompleted)throw new InvalidOperationException("原生命令已发送，但后续目标核对失败；结果未确认，不会重发。详情："+e.Message,e);
                    throw;
                }
            }
            bool ExpectedTransition(string[] identities) {
                if(pendingRemovalName!=null) {
                    if(identities.Any(id=>id==NameToken(pendingRemovalName)||clearBindings.Any(b=>b.Style.Uuid==id&&b.Style.Name==pendingRemovalName)))return false;
                }
                return !appliedRequested||(identities.Length==1&&identities[0]==binding.Style.Uuid);
            }
            public async Task<string[]> ReadAppliedStyles() {
                if(!IsCurrent)throw new OperationCanceledException("连接已取消。");
                lastObservedStyles=null;
                if(!foregroundPrepared) {
                    ShowWindow(expected.Handle,3);
                    if(!SetForegroundWindow(expected.Handle))throw new InvalidOperationException("不能激活 C1，未发送。");
                    await Task.Delay(180);
                    foregroundPrepared=true;
                }
                RequireActiveWindow(expected,isCurrent);
                progress("正在读取 C1 当前样式…");
                string[] previous=null;
                var timer=Stopwatch.StartNew();
                // The first observation discovers the tool; later observations
                // refresh only that cached subtree. Empty is a successful reading,
                // hidden/missing/provider failure have separate diagnostic codes.
                for(int i=0;i<(commandCompleted?12:4);i++) {
                    if(i>0)await Task.Delay(150);
                    RequireActiveWindow(expected,isCurrent);
                    string[] current;
                    try {current=await ReadStylesBounded(expected,reader=> {
                        Action validate=()=>{reader.OperationGuard();RequireActiveWindow(expected,isCurrent);};
                        reader.Stage("read-identities.begin");
                        var observed=reader.ReadIdentities(clearBindings,validate);
                        reader.Stage("read-identities.end count="+observed.Length);
                        // This is transaction evidence, not remembered identity:
                        // after this session invoked an exact catalog path, the
                        // new native row confirms that issued style's display name.
                        if(appliedRequested&&observed.Length==1&&observed[0]==NameToken(binding.Style.Name))observed[0]=binding.Style.Uuid;
                        return observed;
                    });}
                    catch(Exception e) {
                        if(commandCompleted)throw new InvalidOperationException("原生命令已发送，但回读未能确认结果；不会重发。回读详情："+e.Message,e);
                        throw;
                    }
                    if(binding!=null&&i==0) {
                        // Discovery/list evidence and exact-path preparation have
                        // independent bounded waits. Both finish before the final
                        // target revalidation; no expensive work moves into Send.
                        try {await ReadStylesBounded(expected,reader=> {
                            reader.Stage("prepare-request.begin");
                            reader.StyleState(binding,()=>{reader.OperationGuard();RequireActiveWindow(expected,isCurrent);});
                            reader.Stage("prepare-request.end");return true;
                        });}
                        catch(Exception e) {
                            if(commandCompleted)throw new InvalidOperationException("原生命令已发送，但后续样式路径准备失败；结果未确认，不会重发。详情："+e.Message,e);
                            throw;
                        }
                    }
                    RequireActiveWindow(expected,isCurrent);
                    if(previous!=null&&previous.SequenceEqual(current,StringComparer.Ordinal)&&ExpectedTransition(current)) {
                        progress("原生样式回读："+(current.Length==0?"已确认空列表":current.Length+" 项")+"；"+timer.ElapsedMilliseconds+" ms（限定工具子树）");
                        if(current.Any(s=>s.StartsWith("rni-name:",StringComparison.Ordinal)))
                            progress("当前样式按已索引 RNI 名称匹配，原生懒加载勾选未提供 UUID；需要切换时会先精确移除该行，不猜标准/颗粒版本。");
                        pendingRemovalName=null;lastObservedStyles=current;return current;
                    }
                    previous=current;
                }
                throw new InvalidOperationException(commandCompleted?"原生命令已发送，但 C1 样式列表未确认预期变化；已停止，不会重发。":"C1 当前样式尚未稳定，已停止；请稍后重试。");
            }
            public async Task SendShortcut() {
                bool oldRemoved=false;
                try {
                    if(lastObservedStyles!=null&&lastObservedStyles.Any(s=>s.StartsWith("rni-name:",StringComparison.Ordinal))) {
                        if(lastObservedStyles.Length!=1)throw new InvalidOperationException("当前是混合样式，未自动替换。");
                        await RemoveCurrentStyle(lastObservedStyles[0]);
                        var cleared=await ReadAppliedStyles();
                        await ValidateTarget();
                        if(cleared.Length!=0)throw new InvalidOperationException("原生行清除后未确认空列表，未继续应用新样式。");
                        oldRemoved=true;
                    }
                    progress("目标与当前样式已核对，正在切换 "+binding.Style.Name+"…");
                    await Send(binding,expected,armed,appRoot,isCurrent,false,primaryOnly);
                    commandCompleted=true;appliedRequested=true;
                }catch(Exception e) {
                    if(oldRemoved)throw new InvalidOperationException("旧 RNI 已移除；新样式尚未确认/未完成，不会自动重试。详情："+e.Message,e);
                    throw;
                }
            }
            public async Task RemoveCurrentStyle(string styleName) {
                if(cleared.Contains(styleName))throw new InvalidOperationException("此样式清除指令已发送，不会重复切换。");
                if(lastObservedStyles==null||lastObservedStyles.Count(s=>s==styleName)!=1)
                    throw new InvalidOperationException("当前样式未能唯一确认，未清除。");
                string display;StyleBinding exact=null;
                if(styleName.StartsWith("rni-name:",StringComparison.Ordinal)) {
                    display=styleName.Substring("rni-name:".Length);
                    if(!clearBindings.Any(b=>b.Style.Name==display))throw new InvalidOperationException("样式名称不在 RNI 索引中，未清除。");
                } else {
                    var matches=clearBindings.Where(b=>String.Equals(b.Style.Uuid,styleName,StringComparison.Ordinal)).ToArray();
                    if(matches.Length!=1)throw new InvalidOperationException("当前样式没有唯一的已索引身份，未清除。");
                    exact=matches[0];display=exact.Style.Name;
                }
                progress((exact==null?"正在通过原生样式行清除 ":"正在清除已核对的原生样式 ")+display+"…");
                await ValidateTarget();RequireActiveWindow(expected,isCurrent);
                cleared.Add(styleName);
                if(exact!=null)await Send(exact,expected,armed,appRoot,isCurrent,true,primaryOnly);
                else await ReadStylesBounded(expected,reader=>{if(primaryOnly)RequirePrimaryOnly(expected);reader.RemoveApplied(display,expected,isCurrent);return true;},true);
                commandCompleted=true;pendingRemovalName=display;
            }
        }
        public static async Task<StyleApplyResult> Apply(StyleBinding binding,TargetSnapshot expected,TargetSnapshot armed,string appRoot,
            Func<bool> isCurrent,IEnumerable<StyleBinding> bindings,Action<string> progress) {
            var available=bindings.Where(b=>b!=null&&b.Style!=null).ToArray();
            var result=await StyleWorkflow.Run(new LiveStyleSession(binding,expected,armed,appRoot,isCurrent,progress,available),binding.Style.Uuid,KnownStyleTokens(available));
            result.ConfirmedStyle=binding.Style.Name;return result;
        }
        public static async Task<StyleClearResult> Clear(TargetSnapshot expected,TargetSnapshot armed,string appRoot,
            Func<bool> isCurrent,IEnumerable<StyleBinding> bindings,Action<string> progress) {
            var available=(bindings??Enumerable.Empty<StyleBinding>()).Where(b=>b!=null&&b.Style!=null).ToArray();
            var result=await StyleClearWorkflow.Run(new LiveStyleSession(null,expected,armed,appRoot,isCurrent,progress,available),KnownStyleTokens(available));
            if(result.RemovedStyles!=null)result.RemovedStyle=String.Join("、",result.RemovedStyles.Select(id=>id.StartsWith("rni-name:",StringComparison.Ordinal)?id.Substring("rni-name:".Length):available.First(b=>b.Style.Uuid==id).Style.Name));
            return result;
        }
        static async Task Send(StyleBinding binding, TargetSnapshot expected, TargetSnapshot armed, string appRoot,Func<bool> isCurrent,bool remove=false,bool primaryOnly=false) {
            string reason=primaryOnly?SafetyPolicy.CheckPrimary(expected,armed):SafetyPolicy.Check(expected,armed);
            if(reason!=null) throw new InvalidOperationException(reason);
            if(!isCurrent())throw new OperationCanceledException("连接已取消，未发送。");
            // User-initiated panel action only. There is deliberately no CLI/API
            // endpoint for sending arbitrary input or bypassing these guards.
            foreach(int modifier in new []{0x10,0x11,0x12}) if((GetAsyncKeyState(modifier)&0x8000)!=0) throw new InvalidOperationException("请先松开 Shift / Ctrl / Alt。");
            RequireActiveWindow(expected,isCurrent);
            var current=await Task.Run(()=>primaryOnly?InspectPrimary(appRoot):Inspect(appRoot));
            reason=primaryOnly?SafetyPolicy.CheckPrimary(current,armed):SafetyPolicy.Check(current,armed);
            if(reason!=null || current.Handle!=expected.Handle) throw new InvalidOperationException(reason??"C1 窗口已改变。");
            if(!isCurrent())throw new OperationCanceledException("连接已取消，未发送。");
            if(GetForegroundWindow()!=current.Handle) throw new InvalidOperationException("焦点不在 C1，未发送。");
            var settings=CaptureSettings.Read(appRoot);
            if(!settings.ReplaceStyles||settings.AutoSyncMetadata!="None")
                throw new InvalidOperationException("C1 连接设置或元数据同步条件已改变，未发送。");
            if(primaryOnly)await Task.Run(()=>RequirePrimaryOnly(expected));
            var focused=AutomationElement.FocusedElement;
            if(focused!=null && focused.Current.ControlType==ControlType.Edit) throw new InvalidOperationException("C1 当前焦点在文字输入框，已阻止。");
            if(binding.Shortcut==0||settings.ShortcutName!=Shortcuts.SetName) {
                await ReadStylesBounded(expected,reader=>{reader.ToggleStyle(binding,remove,()=>{RequireActiveWindow(expected,isCurrent);if(primaryOnly)RequirePrimaryOnly(expected);});return true;},true);
                return;
            }
            ushort key=(ushort)(binding.Shortcut&0xffff);
            var modifiers=new List<ushort>();
            if((binding.Shortcut&0x20000)!=0)modifiers.Add(0x11);
            if((binding.Shortcut&0x40000)!=0)modifiers.Add(0x12);
            if((binding.Shortcut&0x10000)!=0)modifiers.Add(0x10);
            var sequence=modifiers.Concat(new[]{key,key}).Concat(modifiers.AsEnumerable().Reverse()).ToArray();
            var inputs=new INPUT[sequence.Length];
            for(int i=0;i<inputs.Length;i++) inputs[i]=new INPUT {Type=1,Data=new INPUTUNION {Keyboard=new KEYBDINPUT {Vk=sequence[i],Flags=(uint)(i>modifiers.Count?2:0)}}};
            if(!isCurrent())throw new OperationCanceledException("连接已取消，未发送。");
            uint sent=SendInput((uint)inputs.Length,inputs,Marshal.SizeOf(typeof(INPUT)));
            if(sent!=inputs.Length) throw new InvalidOperationException("系统未完整接收按键，结果未知；不自动重试。请回 C1 检查。");
        }
    }
}
