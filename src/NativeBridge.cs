using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
    public static class CaptureOneBridge {
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint Type; public INPUTUNION Data; }
        [StructLayout(LayoutKind.Explicit)] struct INPUTUNION {
            [FieldOffset(0)] public KEYBDINPUT Keyboard;
            [FieldOffset(0)] public MOUSEINPUT Mouse;
        }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT {public ushort Vk;public ushort Scan;public uint Flags;public uint Time;public UIntPtr ExtraInfo;}
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT {public int Dx;public int Dy;public uint MouseData;public uint Flags;public uint Time;public UIntPtr ExtraInfo;}
        static string GetDocumentPath(AutomationElement root,string title) {
            var paths=new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selector=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"DocumentSelector"));
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
        public static TargetSnapshot Inspect(string appRoot) {
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
            var root=AutomationElement.FromHandle(handle);
            state.DocumentPath=GetDocumentPath(root,state.Title);
            var browser=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"MainItemsControl"));
            if(browser==null) throw new InvalidOperationException("未找到 C1 照片浏览器；请关闭弹窗并显示浏览器。");
            object selection;
            AutomationElement[] selected;
            if(browser.TryGetCurrentPattern(SelectionPattern.Pattern,out selection)) selected=((SelectionPattern)selection).Current.GetSelection();
            else selected=browser.FindAll(TreeScope.Descendants,new PropertyCondition(SelectionItemPattern.IsSelectedProperty,true)).Cast<AutomationElement>().ToArray();
            var variants=selected.Select(e=>e.Current.Name).Where(n=>Regex.IsMatch(n??"",@"VariantID:\s*\d+")).Distinct().ToArray();
            state.Diagnostics+="; selectionPattern="+(selection!=null)+"; rawSelected="+selected.Length+"; names="+String.Join(" | ",selected.Select(e=>e.Current.Name))+"; browserOffscreen="+browser.Current.IsOffscreen;
            var summary=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"SummaryText"));
            if(summary!=null)state.Diagnostics+="; summary="+summary.Current.Name;
            state.SelectedCount=variants.Length;
            if(variants.Length<=1 && summary!=null && SelectionSummary.IsSingle(summary.Current.Name)) {
                var titles=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"tb_VariantTitle"))
                    .Cast<AutomationElement>().Where(e=>!e.Current.IsOffscreen&&!String.IsNullOrWhiteSpace(e.Current.Name)).Select(e=>e.Current.Name).ToArray();
                if(titles.Length!=1)throw new InvalidOperationException("请使用单图查看器；不能唯一确定当前照片标题。");
                string filename=titles[0];int index=0;
                var number=Regex.Match(filename,@"^(.*?)\s+\[(\d+)\]$");
                if(number.Success){filename=number.Groups[1].Value;index=int.Parse(number.Groups[2].Value)-1;}
                if(index<0)throw new InvalidOperationException("变体序号无效。");
                var identity=CatalogReader.ResolveSingle(state.DocumentPath,filename);
                string peerName="Variant: "+identity.FileName+" - VariantID: "+identity.Id;
                if(variants.Length==1&&!String.Equals(variants[0],peerName,StringComparison.Ordinal))
                    throw new InvalidOperationException("当前选区与查看器不是同一张照片，未发送。");
                var peer=browser.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,peerName));
                if(peer==null)throw new InvalidOperationException("查看器、图库记录与浏览器条目不一致，未发送。");
                state.VariantId=identity.Id;state.VariantUuid=identity.Uuid;state.VariantName=peerName;state.FileName=identity.FileName;state.SelectedCount=1;
                state.Diagnostics+="; adapter=single-selection-summary + one-viewer-caption + globally-single-variant-readonly-db + browser-peer; uuid="+identity.Uuid;
            }
            else state.SelectedCount=variants.Length>1?variants.Length:0;
            if(state.SelectedCount!=1)state.Diagnostics+="; a single selected image and one viewer are required";
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
        }
        static bool IsStylesToolName(string name) {
            name=(name??"").Trim();
            return name=="样式与预设"||String.Equals(name,"Styles and Presets",StringComparison.OrdinalIgnoreCase)||
                String.Equals(name,"Styles & Presets",StringComparison.OrdinalIgnoreCase);
        }
        static bool HasOwnVisibleStylesHeader(AutomationElement tool) {
            int matches=0;
            foreach(AutomationElement header in tool.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty,"headerTextBlock"))) {
                if(header.Current.IsOffscreen||!IsStylesToolName(header.Current.Name))continue;
                var owner=TreeWalker.ControlViewWalker.GetParent(header);
                for(int depth=0;owner!=null&&depth<16;depth++) {
                    if(Automation.Compare(owner,tool)){matches++;break;}
                    // A nested tool/group owns its own header, not this tool's.
                    if(owner.Current.AutomationId=="expander"||owner.Current.ControlType==ControlType.Group)break;
                    owner=TreeWalker.ControlViewWalker.GetParent(owner);
                }
            }
            return matches==1;
        }
        static bool IsVisibleExpandedEmptyStyleList(AutomationElement list) {
            // An empty applied-style list can collapse its own bounds. Accept that
            // only with live proof that its own named tool is visible and expanded.
            var bounds=list.Current.BoundingRectangle;
            if(!bounds.IsEmpty&&bounds.Width>0&&bounds.Height>0)return false;
            if(list.FindFirst(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem))!=null)return false;
            var parent=TreeWalker.ControlViewWalker.GetParent(list);
            for(int depth=0;parent!=null&&depth<16;depth++) {
                if(parent.Current.ControlType==ControlType.Window)return false;
                object expansion;
                bool expandable=parent.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out expansion);
                bool tool=parent.Current.AutomationId=="expander"||
                    (expandable&&(parent.Current.ControlType==ControlType.Group||IsStylesToolName(parent.Current.Name)));
                if(expandable&&((ExpandCollapsePattern)expansion).Current.ExpandCollapseState!=ExpandCollapseState.Expanded)return false;
                if(tool) {
                    if(!expandable)return false;
                    var toolBounds=parent.Current.BoundingRectangle;
                    if(parent.Current.IsOffscreen||!parent.Current.IsEnabled||toolBounds.IsEmpty||toolBounds.Width<=0||toolBounds.Height<=0)return false;
                    // Stop at the nearest tool: never borrow a header from another
                    // expander or search a page/window-wide ancestor for a match.
                    return IsStylesToolName(parent.Current.Name)||HasOwnVisibleStylesHeader(parent);
                }
                parent=TreeWalker.ControlViewWalker.GetParent(parent);
            }
            return false;
        }
        static string[] ReadNativeStyleList(TargetSnapshot expected) {
            var root=AutomationElement.FromHandle(expected.Handle);
            var lists=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"ListViewAppliedStyles"))
                .Cast<AutomationElement>().Where(e=>e.Current.IsEnabled&&(!e.Current.IsOffscreen||IsVisibleExpandedEmptyStyleList(e))).ToArray();
            if(lists.Length!=1)throw new InvalidOperationException("请在 C1“图库”页添加并展开“样式与预设”，让已应用列表可见；面板不会切页。");
            object scroll;
            if(lists[0].TryGetCurrentPattern(ScrollPattern.Pattern,out scroll)&&((ScrollPattern)scroll).Current.VerticallyScrollable)
                throw new InvalidOperationException("已应用列表含不可完整读取的滚动项，未发送。");
            var result=new List<string>();
            foreach(AutomationElement row in lists[0].FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.ListItem))) {
                if(row.Current.IsOffscreen)throw new InvalidOperationException("已应用样式有隐藏条目，未发送。");
                var names=row.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Text))
                    .Cast<AutomationElement>().Select(e=>e.Current.Name).Where(n=>!String.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToArray();
                if(names.Length!=1)throw new InvalidOperationException("当前样式名称不能唯一读取，未发送。");
                result.Add(names[0]);
            }
            return result.ToArray();
        }
        sealed class LiveStyleSession:ILiveStyleSession,IClearStyleSession {
            readonly StyleBinding binding;
            readonly StyleBinding[] clearBindings;
            readonly TargetSnapshot expected,armed;
            readonly string appRoot;
            readonly Func<bool> isCurrent;
            readonly Action<string> progress;
            bool foregroundPrepared;
            bool clearSent;
            string[] lastObservedStyles;
            public bool IsCurrent { get {return isCurrent();} }
            public LiveStyleSession(StyleBinding b,TargetSnapshot e,TargetSnapshot a,string root,Func<bool> current,Action<string> report,
                IEnumerable<StyleBinding> removable=null) {
                binding=b;expected=e;armed=a;appRoot=root;isCurrent=current;progress=report;
                clearBindings=(removable??Enumerable.Empty<StyleBinding>()).ToArray();
            }
            public async Task ValidateTarget() {
                if(!IsCurrent)throw new OperationCanceledException("连接已取消。");
                var current=await Task.Run(()=>Inspect(appRoot));
                string reason=SafetyPolicy.Check(current,armed);
                if(reason!=null||!SafetyPolicy.IsAllowedCatalog(current.DocumentPath))
                    throw new InvalidOperationException(reason??"当前不是支持的图库，未发送。");
                if(foregroundPrepared)RequireActiveWindow(expected,isCurrent);
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
                // Read only the visible tool, never select a C1 tab. Two matching
                // observations allow rendering to settle; unavailable tools fail immediately.
                for(int i=0;i<12;i++) {
                    await Task.Delay(100);
                    RequireActiveWindow(expected,isCurrent);
                    var read=Task.Run(()=>ReadNativeStyleList(expected));
                    if(await Task.WhenAny(read,Task.Delay(2000))!=read)
                        throw new TimeoutException("C1 当前样式读取超时，已停止，不会重发。");
                    var current=await read;
                    RequireActiveWindow(expected,isCurrent);
                    if(previous!=null&&previous.SequenceEqual(current,StringComparer.Ordinal)) {
                        lastObservedStyles=current;return current;
                    }
                    previous=current;
                }
                throw new InvalidOperationException("C1 当前样式尚未稳定，已停止；请稍后重试。");
            }
            public async Task SendShortcut() {
                progress("目标与当前样式已核对，正在切换 "+binding.Style.Name+"…");
                await Send(binding,expected,armed,appRoot,isCurrent);
            }
            public async Task RemoveCurrentStyle(string styleName) {
                if(clearSent)throw new InvalidOperationException("清除指令已发送，不会重复切换。");
                if(lastObservedStyles==null||lastObservedStyles.Length!=1||!String.Equals(lastObservedStyles[0],styleName,StringComparison.Ordinal))
                    throw new InvalidOperationException("当前样式未能唯一确认，未清除。");
                var matches=clearBindings.Where(b=>b!=null&&b.Style!=null&&String.Equals(b.Style.Name,styleName,StringComparison.Ordinal)).ToArray();
                if(matches.Length!=1)throw new InvalidOperationException("当前样式没有唯一的已连接快捷键，未清除。");
                progress("正在清除 "+styleName+"…");
                // The same native style command toggles off the confirmed current
                // style. Send once only; never use reset-all or retry on uncertainty.
                clearSent=true;
                await Send(matches[0],expected,armed,appRoot,isCurrent);
            }
        }
        public static Task<StyleApplyResult> Apply(StyleBinding binding,TargetSnapshot expected,TargetSnapshot armed,string appRoot,
            Func<bool> isCurrent,IEnumerable<string> supported,Action<string> progress) {
            return StyleWorkflow.Run(new LiveStyleSession(binding,expected,armed,appRoot,isCurrent,progress),binding.Style.Name,supported);
        }
        public static Task<StyleClearResult> Clear(TargetSnapshot expected,TargetSnapshot armed,string appRoot,
            Func<bool> isCurrent,IEnumerable<StyleBinding> bindings,Action<string> progress) {
            var available=(bindings??Enumerable.Empty<StyleBinding>()).Where(b=>b!=null&&b.Style!=null).ToArray();
            return StyleClearWorkflow.Run(new LiveStyleSession(null,expected,armed,appRoot,isCurrent,progress,available),
                available.Select(b=>b.Style.Name));
        }
        static async Task Send(StyleBinding binding, TargetSnapshot expected, TargetSnapshot armed, string appRoot,Func<bool> isCurrent) {
            string reason=SafetyPolicy.Check(expected,armed);
            if(reason!=null) throw new InvalidOperationException(reason);
            if(!isCurrent())throw new OperationCanceledException("连接已取消，未发送。");
            // User-initiated panel action only. There is deliberately no CLI/API
            // endpoint for sending arbitrary input or bypassing these guards.
            foreach(int modifier in new []{0x10,0x11,0x12}) if((GetAsyncKeyState(modifier)&0x8000)!=0) throw new InvalidOperationException("请先松开 Shift / Ctrl / Alt。");
            RequireActiveWindow(expected,isCurrent);
            var current=await Task.Run(()=>Inspect(appRoot));
            reason=SafetyPolicy.Check(current,armed);
            if(reason!=null || current.Handle!=expected.Handle) throw new InvalidOperationException(reason??"C1 窗口已改变。");
            if(!isCurrent())throw new OperationCanceledException("连接已取消，未发送。");
            if(GetForegroundWindow()!=current.Handle) throw new InvalidOperationException("焦点不在 C1，未发送。");
            var settings=CaptureSettings.Read(appRoot);
            if(settings.ShortcutName!=Shortcuts.SetName||!settings.ReplaceStyles||settings.AutoSyncMetadata!="None")
                throw new InvalidOperationException("C1 连接设置或元数据同步条件已改变，未发送。");
            var focused=AutomationElement.FocusedElement;
            if(focused!=null && focused.Current.ControlType==ControlType.Edit) throw new InvalidOperationException("C1 当前焦点在文字输入框，已阻止。");
            ushort key=(ushort)(binding.Shortcut&0xffff);
            ushort[] sequence={0x11,0x12,0x10,key,key,0x10,0x12,0x11};
            var inputs=new INPUT[sequence.Length];
            for(int i=0;i<inputs.Length;i++) inputs[i]=new INPUT {Type=1,Data=new INPUTUNION {Keyboard=new KEYBDINPUT {Vk=sequence[i],Flags=(uint)(i>=4?2:0)}}};
            if(!isCurrent())throw new OperationCanceledException("连接已取消，未发送。");
            uint sent=SendInput((uint)inputs.Length,inputs,Marshal.SizeOf(typeof(INPUT)));
            if(sent!=inputs.Length) throw new InvalidOperationException("系统未完整接收按键，结果未知；不自动重试。请回 C1 检查。");
        }
    }
}
