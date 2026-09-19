using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace RniPanel {
    public static partial class CaptureOneBridge {
        // This is a native-menu adapter, not a Capture One selection/style API.
        // EditMultiple is always observed live; user.config is not authoritative.
        static AutomationElement batchEditMode;
        static IntPtr batchEditHandle;
        static long batchEditProcessStart;
        static bool batchEditUsesMenu;
        static IntPtr batchMenuHandle;
        static long batchMenuProcessStart;
        static AutomationElement batchMenuBar;
        static readonly Dictionary<string,AutomationElement> batchMenuHeaders=new Dictionary<string,AutomationElement>(StringComparer.Ordinal);
        [StructLayout(LayoutKind.Sequential)] struct AccessibilityPoint {public int X,Y;}
        [DllImport("oleacc.dll")] static extern int AccessibleObjectFromPoint(AccessibilityPoint point,
            [MarshalAs(UnmanagedType.Interface)] out Accessibility.IAccessible accessible,
            [MarshalAs(UnmanagedType.Struct)] out object child);
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(AccessibilityPoint point);
        sealed class NativeCheckObservation {
            public readonly AutomationElement Element;
            public readonly bool Value;
            public NativeCheckObservation(AutomationElement element,bool value){Element=element;Value=value;}
        }

        static string MenuName(string name) {
            return (name??"").Split('\t')[0].Replace("&","").Replace("(_I)","").Replace("(I)","").Trim();
        }
        static AutomationElement[] MatchBatchControl(AutomationElement root,string id,HashSet<string> wanted) {
            var timer=System.Diagnostics.Stopwatch.StartNew();
            var cache=new CacheRequest {TreeScope=TreeScope.Element,AutomationElementMode=AutomationElementMode.Full};
            cache.Add(AutomationElement.NameProperty);cache.Add(AutomationElement.AutomationIdProperty);cache.Add(AutomationElement.IsOffscreenProperty);
            AutomationElement[] items;
            using(cache.Activate())items=root.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem)).Cast<AutomationElement>().ToArray();
            LogNativeTiming("batch-menu match.children id="+id+"; count="+items.Length+"; ms="+timer.ElapsedMilliseconds);
            // MenuBar and popup menu commands are direct children. Do not
            // descend into every native submenu (including huge style menus).
            if(items.Length==0)using(cache.Activate())items=root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem)).Cast<AutomationElement>().ToArray();
            var matches=items.Where(e=>!e.Cached.IsOffscreen&&
                (e.Cached.AutomationId==id||wanted.Contains(MenuName(e.Cached.Name)))).ToArray();
            LogNativeTiming("batch-menu match.end id="+id+"; matches="+matches.Length+"; ms="+timer.ElapsedMilliseconds);
            return matches;
        }
        static AutomationElement[] MatchBatchPopups(AutomationElement root,string id,HashSet<string> wanted) {
            int process=root.Current.ProcessId,mainHandle=root.Current.NativeWindowHandle;
            var timer=System.Diagnostics.Stopwatch.StartNew();
            var matches=new List<AutomationElement>();
            var windows=AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ProcessIdProperty,process));
            LogNativeTiming("batch-menu popup.enumerated id="+id+"; count="+windows.Count+"; ms="+timer.ElapsedMilliseconds);
            foreach(AutomationElement popup in windows) {
                if(popup.Current.NativeWindowHandle==mainHandle||popup.Current.IsOffscreen)continue;
                var menu=popup.Current.ControlType==ControlType.Menu?popup:popup.FindFirst(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Menu));
                if(menu==null)continue;
                matches.AddRange(MatchBatchControl(menu,id,wanted));
            }
            LogNativeTiming("batch-menu popup.end id="+id+"; matches="+matches.Count+"; ms="+timer.ElapsedMilliseconds);
            return matches.ToArray();
        }
        static AutomationElement FindBatchControl(AutomationElement root,string id,params string[] names) {
            var wanted=new HashSet<string>(names.Select(MenuName),StringComparer.OrdinalIgnoreCase);
            // Known child commands are requested only after their menu opens.
            // Search that small popup before the expanded main style tree.
            bool child=id=="editPrimaryOnlyToolStripMenuItem"||id=="selectFirstToolStripMenuItem"||id=="selectNextToolStripMenuItem"||
                id=="customizeViewerToolStripMenuItem"||id=="viewerModeShowAllToolStripMenuItem";
            var matches=child?MatchBatchPopups(root,id,wanted):MatchBatchControl(root,id,wanted);
            if(matches.Length==0)matches=child?MatchBatchControl(root,id,wanted):MatchBatchPopups(root,id,wanted);
            if(matches.Length==1)return matches[0];
            throw new InvalidOperationException("无法唯一找到 C1 原生命令 "+id+"；批量未继续。");
        }
        static AutomationElement OpenBatchMenu(TargetSnapshot target,string id,params string[] names) {
            var timer=System.Diagnostics.Stopwatch.StartNew();
            AutomationElement menu=null;
            bool top=id=="imageToolStripMenuItem"||id=="selectToolStripMenuItem"||id=="viewToolStripMenuItem";
            if(top) {
                if(batchMenuHandle!=target.Handle||batchMenuProcessStart!=target.ProcessStartTicks) {
                    batchMenuHeaders.Clear();batchMenuBar=null;
                    batchMenuHandle=target.Handle;batchMenuProcessStart=target.ProcessStartTicks;
                }
                var wanted=new HashSet<string>(names.Select(MenuName),StringComparer.OrdinalIgnoreCase);
                if(batchMenuHeaders.TryGetValue(id,out menu))try {
                    var info=menu.Current;
                    if(info.ProcessId!=target.ProcessId||info.IsOffscreen||!info.IsEnabled||
                        info.ControlType!=ControlType.MenuItem||!wanted.Contains(MenuName(info.Name)))menu=null;
                }catch(ElementNotAvailableException){menu=null;}
                LogNativeTiming("batch-menu header-cache id="+id+"; hit="+(menu!=null)+"; ms="+timer.ElapsedMilliseconds);
                if(menu==null) {
                    batchMenuHeaders.Remove(id);
                    var root=AutomationElement.FromHandle(target.Handle);
                    if(batchMenuBar!=null)try {
                        if(batchMenuBar.Current.ProcessId!=target.ProcessId||batchMenuBar.Current.IsOffscreen)batchMenuBar=null;
                    }catch(ElementNotAvailableException){batchMenuBar=null;}
                    if(batchMenuBar==null)batchMenuBar=root.FindFirst(TreeScope.Children,
                        new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuBar));
                    if(batchMenuBar==null)batchMenuBar=root.FindFirst(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuBar));
                    if(batchMenuBar!=null) {
                        var matches=MatchBatchControl(batchMenuBar,id,wanted);
                        if(matches.Length>1)throw new InvalidOperationException("C1 顶层菜单不唯一，批量已停止。");
                        if(matches.Length==1)menu=matches[0];
                    }
                    if(menu==null)menu=FindBatchControl(root,id,names);
                    batchMenuHeaders[id]=menu;
                }
            } else menu=FindBatchControl(AutomationElement.FromHandle(target.Handle),id,names);
            if(!menu.Current.IsEnabled)throw new InvalidOperationException("C1 原生菜单不可操作，批量已停止。");
            LogNativeTiming("batch-menu open.begin id="+id+"; ms="+timer.ElapsedMilliseconds);
            object pattern;
            if(menu.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out pattern))
                ((ExpandCollapsePattern)pattern).Expand();
            else if(menu.TryGetCurrentPattern(InvokePattern.Pattern,out pattern))
                ((InvokePattern)pattern).Invoke();
            else throw new InvalidOperationException("C1 原生菜单未提供可用展开动作，批量已停止。");
            LogNativeTiming("batch-menu open.end id="+id+"; ms="+timer.ElapsedMilliseconds);
            return menu;
        }
        static void CloseBatchMenu(AutomationElement menu) {
            object pattern;
            if(menu!=null&&menu.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out pattern)) {
                var expand=(ExpandCollapsePattern)pattern;
                if(expand.Current.ExpandCollapseState!=ExpandCollapseState.Collapsed)expand.Collapse();
            }
        }
        static bool ReadBatchEditMode(AutomationElement element) {
            if(element==null)throw new InvalidOperationException("未取得 C1 实时多图编辑开关，批量已停止。");
            object pattern;
            if(element.TryGetCurrentPattern(TogglePattern.Pattern,out pattern)) {
                var state=((TogglePattern)pattern).Current.ToggleState;
                if(state==ToggleState.On)return true;
                if(state==ToggleState.Off)return false;
            }
            // An unsupported property is unknown, never an implicit false.
            object value=element.GetCurrentPropertyValue(TogglePattern.ToggleStateProperty,true);
            if(value is ToggleState) {
                if((ToggleState)value==ToggleState.On)return true;
                if((ToggleState)value==ToggleState.Off)return false;
            }
            LogNativeTiming("batch-mode no-uia-toggle; kind="+element.Current.ControlType.ProgrammaticName+
                "; id="+element.Current.AutomationId+"; name="+element.Current.Name+"; hidden="+element.Current.IsOffscreen);
            if(element.Current.ControlType==ControlType.MenuItem)return ReadNativeMenuChecked(element);
            throw new InvalidOperationException("C1 多图编辑开关没有公开可靠勾选状态，未开始批量发送。");
        }
        static bool ReadNativeMenuChecked(AutomationElement element) {
            // Official Windows MSAA fallback for this already-resolved visible
            // menu row, not a guessed UIA default or a saved C1 setting.
            var info=element.Current;var bounds=info.BoundingRectangle;
            if(info.ControlType!=ControlType.MenuItem||info.IsOffscreen||bounds.IsEmpty||bounds.Width<2||bounds.Height<2)
                throw new InvalidOperationException("原生菜单行不可见，不能读取实时勾选状态。");
            var point=new AccessibilityPoint {X=(int)(bounds.X+bounds.Width/2),Y=(int)(bounds.Y+bounds.Height/2)};
            uint owner;GetWindowThreadProcessId(WindowFromPoint(point),out owner);
            if(owner!=info.ProcessId)throw new InvalidOperationException("原生菜单被其他窗口遮挡，未读取或切换模式。");
            Accessibility.IAccessible accessible=null;object child;
            try {
                int result=AccessibleObjectFromPoint(point,out accessible,out child);
                if(result!=0||accessible==null||!(child is int))
                    throw new InvalidOperationException("Windows 未提供当前菜单的可访问对象，不能判断勾选状态。");
                string name=accessible.get_accName(child);
                object roleValue=accessible.get_accRole(child),stateValue=accessible.get_accState(child);
                if(!(roleValue is int)||!(stateValue is int))
                    throw new InvalidOperationException("原生菜单没有提供有效角色或状态，未推断为关闭。");
                int role=(int)roleValue,state=(int)stateValue;
                LogNativeTiming("batch-mode msaa; child="+child+"; name="+name+"; role="+role+"; state=0x"+state.ToString("X"));
                if(role!=0x0c||!String.Equals(MenuName(name),MenuName(info.Name),StringComparison.Ordinal))
                    throw new InvalidOperationException("Windows 菜单对象与已定位命令不一致，未判断或切换模式。");
                if((state&(0x1|0x20|0x8000|0x10000))!=0)
                    throw new InvalidOperationException("原生菜单状态不可用、混合或不可见，不能判断实时模式。");
                return (state&0x10)!=0; // STATE_SYSTEM_CHECKED, not focus/selection.
            } finally {if(accessible!=null&&Marshal.IsComObject(accessible))Marshal.ReleaseComObject(accessible);}
        }
        static void LogBatchMode(string phase,AutomationElement element,bool value) {
            try {
                var info=element.Current;
                LogNativeTiming("batch-mode "+phase+"; value="+value+"; kind="+info.ControlType.ProgrammaticName+
                    "; id="+info.AutomationId+"; name="+info.Name+"; hidden="+info.IsOffscreen+
                    "; invoke="+element.GetCurrentPropertyValue(AutomationElement.IsInvokePatternAvailableProperty)+
                    "; toggle="+element.GetCurrentPropertyValue(AutomationElement.IsTogglePatternAvailableProperty));
            } catch(ElementNotAvailableException) {LogNativeTiming("batch-mode "+phase+"; value="+value+"; freshly-read popup is now closed");}
        }
        static void ChangeBatchEditMode(AutomationElement element,TargetSnapshot target,Func<bool> current,bool before) {
            AutomationElement menu=null;
            bool clicked=false;
            try {
                RequireActiveWindow(target,current);
                // The fallback menu returned by the observer is closed again.
                // Reopen before acting: invoking an offscreen WinForms item can
                // return without executing C1's native command.
                if(batchEditUsesMenu||element.Current.ControlType==ControlType.MenuItem||element.Current.IsOffscreen) {
                    menu=OpenBatchMenu(target,"imageToolStripMenuItem","图像","图像(I)","Image");
                    element=FindBatchControl(AutomationElement.FromHandle(target.Handle),"editPrimaryOnlyToolStripMenuItem",
                        "编辑所有已选项","编辑所有已选变体","Edit All Selected Variants");
                }
                bool actual=ReadBatchEditMode(element);
                LogBatchMode("before-click",element,actual);
                if(actual!=before)throw new InvalidOperationException("C1 多图编辑开关在准备期间改变，未发送切换。");
                RequireStyleMenu(target,current);
                // One native mouse click, never an Invoke-then-click retry.
                ClickNativeRow(element);
                clicked=true;
                batchEditMode=null;
            } finally {
                // Do not race queued mouse-up by forcibly collapsing a menu
                // immediately after SendInput. Its native click closes it.
                if(!clicked)CloseBatchMenu(menu);
            }
        }
        static NativeCheckObservation GetBatchEditMode(TargetSnapshot target) {
            if(batchEditHandle!=target.Handle||batchEditProcessStart!=target.ProcessStartTicks){batchEditMode=null;batchEditUsesMenu=false;}
            if(batchEditMode!=null&&batchEditHandle==target.Handle&&batchEditProcessStart==target.ProcessStartTicks) {
                try {
                    // C1 refreshes WinForms menu checks on opening. A closed
                    // menu's element must not be reused as live edit-mode state.
                    if(batchEditMode.Current.ControlType!=ControlType.MenuItem&&batchEditMode.Current.IsEnabled&&!batchEditMode.Current.IsOffscreen)
                        return new NativeCheckObservation(batchEditMode,ReadBatchEditMode(batchEditMode));
                    batchEditMode=null;
                }catch(ElementNotAvailableException) {batchEditMode=null;}
            }
            bool actual=false;
            // This installation's live mode is exposed by the native menu.
            // Do not repeatedly scan the expanded style tree for an absent
            // toolbar ID. A previously known visible toolbar may be reused
            // above, but no new mode value is inferred or cached here.
            if(batchEditMode==null) {
                batchEditUsesMenu=true;
                var menu=OpenBatchMenu(target,"imageToolStripMenuItem","图像","图像(I)","Image");
                try {
                    batchEditMode=FindBatchControl(AutomationElement.FromHandle(target.Handle),"editPrimaryOnlyToolStripMenuItem",
                        "编辑所有已选项","编辑所有已选变体","Edit All Selected Variants");
                    actual=ReadBatchEditMode(batchEditMode);
                } finally {CloseBatchMenu(menu);}
            }
            batchEditHandle=target.Handle;batchEditProcessStart=target.ProcessStartTicks;
            // MSAA was read while the exact menu row was still visible. The
            // observation is used immediately, never as a subsequent-call cache.
            return new NativeCheckObservation(batchEditMode,actual);
        }
        static void RequirePrimaryOnly(TargetSnapshot target) {
            if(GetBatchEditMode(target).Value)
                throw new InvalidOperationException("C1 已恢复多图同时编辑，批量已停止，未向整个选区发送样式切换。");
        }

        sealed class NativeBatchSession:IBatchStyleSession {
            readonly TargetSnapshot connected;
            readonly string appRoot;
            readonly Func<bool> current;
            readonly StyleBinding requested;
            readonly StyleBinding[] bindings;
            readonly Action<string> progress;
            bool prepared,modeCaptured,originalEditMultiple;
            public bool IsCurrent {get{return current();}}
            public NativeBatchSession(StyleBinding binding,TargetSnapshot observed,string root,Func<bool> isCurrent,
                IEnumerable<StyleBinding> available,Action<string> report) {
                requested=binding;connected=observed;appRoot=root;current=isCurrent;
                bindings=available.Where(b=>b!=null&&b.Style!=null).ToArray();progress=report??delegate{};
            }
            void RequireWindow() {
                if(!current())throw new OperationCanceledException("批量已取消。");
                if((GetAsyncKeyState(0x1b)&0x8000)!=0)throw new OperationCanceledException("检测到 Esc，批量已停止。");
                RequireActiveWindow(connected,current);
            }
            public async Task<TargetSnapshot> InspectPrimary() {
                if(!current())throw new OperationCanceledException("批量已取消。");
                if(prepared)RequireWindow();
                var value=await Task.Run(()=>CaptureOneBridge.InspectPrimary(appRoot));
                string reason=SafetyPolicy.CheckDocument(value,connected);
                if(reason!=null)throw new InvalidOperationException(reason);
                if(value.SelectedCount!=connected.SelectedCount)throw new InvalidOperationException("批量期间选区数量改变，已停止。");
                return value;
            }
            public async Task BeginPrimaryOnly() {
                if(!current())throw new OperationCanceledException("批量已取消。");
                ShowWindow(connected.Handle,3);
                if(!SetForegroundWindow(connected.Handle))throw new InvalidOperationException("不能激活 C1，未开始批量。");
                await Task.Delay(180);prepared=true;RequireWindow();
                await InspectPrimary();
                await Task.Run(()=> {
                    RequireWindow();
                    var mode=GetBatchEditMode(connected);
                    originalEditMultiple=mode.Value;modeCaptured=true;
                    LogBatchMode("begin-observed",mode.Element,originalEditMultiple);
                    if(originalEditMultiple)ChangeBatchEditMode(mode.Element,connected,current,true);
                });
                for(int i=0;i<5;i++) {
                    await Task.Delay(120);RequireWindow();
                    bool actual=await Task.Run(()=> {
                        var mode=GetBatchEditMode(connected);LogBatchMode("after-begin-"+i,mode.Element,mode.Value);return mode.Value;
                    });
                    if(!actual)break;
                    if(i==4)throw new InvalidOperationException("C1 多图编辑开关未确认关闭，已停止，未向选区发送样式。");
                }
                progress("批量：已确认仅编辑主图；正在逐张核对选区，不会向混合选区发送 toggle。");
            }
            async Task Navigate(bool first) {
                RequireWindow();await InspectPrimary();
                await Task.Run(()=> {
                    RequireWindow();
                    var menu=OpenBatchMenu(connected,"selectToolStripMenuItem","选择","Select");
                    try {
                        var item=FindBatchControl(AutomationElement.FromHandle(connected.Handle),
                            first?"selectFirstToolStripMenuItem":"selectNextToolStripMenuItem",
                            first?"第一个":"下一个",first?"First":"Next",first?"Select First":"Select Next");
                        if(!item.Current.IsEnabled)throw new InvalidOperationException("C1 主图导航不可用，批量已停止。");
                        object pattern;
                        if(!item.TryGetCurrentPattern(InvokePattern.Pattern,out pattern))
                            throw new InvalidOperationException("C1 主图导航没有可用原生命令，批量已停止。");
                        // A legitimate open native menu may own foreground;
                        // require the same live C1 process/document, not only
                        // the main HWND, until the command closes its menu.
                        RequireStyleMenu(connected,current);((InvokePattern)pattern).Invoke();
                    } finally {CloseBatchMenu(menu);}
                });
                await Task.Delay(180);RequireWindow();
                // Browser summaries/viewer labels are asynchronous. Require two
                // fresh matching observations before a primary is accepted.
                TargetSnapshot prior=null;
                for(int i=0;i<5;i++) {
                    var value=await InspectPrimary();
                    if(prior!=null&&value.VariantId==prior.VariantId&&value.VariantUuid==prior.VariantUuid)return;
                    prior=value;await Task.Delay(120);
                }
                throw new InvalidOperationException("C1 主图导航尚未稳定，批量已停止。");
            }
            public Task SelectFirst(){return Navigate(true);}
            public Task SelectNext(){return Navigate(false);}
            public async Task<bool> ExecutePrimary(TargetSnapshot expected) {
                RequireWindow();
                var observed=await InspectPrimary();
                string reason=SafetyPolicy.CheckPrimary(observed,expected);
                if(reason!=null)throw new InvalidOperationException(reason);
                var session=new LiveStyleSession(requested,expected,expected,appRoot,current,progress,bindings,true);
                if(requested==null) {
                    var result=await StyleClearWorkflow.Run(session,KnownStyleTokens(bindings));return result.Sent;
                }
                var applied=await StyleWorkflow.Run(session,requested.Style.Uuid,KnownStyleTokens(bindings));return applied.Sent;
            }
            public async Task RestoreEditMode() {
                if(!modeCaptured)return;
                RequireWindow();await InspectPrimary();
                await Task.Run(()=> {
                    RequireWindow();
                    var mode=GetBatchEditMode(connected);
                    bool actual=mode.Value;
                    LogBatchMode("restore-observed",mode.Element,actual);
                    if(actual!=originalEditMultiple)ChangeBatchEditMode(mode.Element,connected,current,actual);
                });
                await Task.Delay(100);RequireWindow();
                if(await Task.Run(()=>GetBatchEditMode(connected).Value)!=originalEditMultiple)
                    throw new InvalidOperationException("照片已逐张确认，但原编辑模式未确认恢复；请查看 C1。");
                await RestorePrimaryViewer(connected,appRoot,current);
            }
        }
        public static Task<BatchStyleResult> ApplySelection(StyleBinding binding,TargetSnapshot observed,string appRoot,
            Func<bool> isCurrent,IEnumerable<StyleBinding> bindings,Action<string> progress,
            Action<int,int,TargetSnapshot,bool> confirmed) {
            if(binding==null)throw new ArgumentNullException("binding");
            return BatchStyleWorkflow.Run(new NativeBatchSession(binding,observed,appRoot,isCurrent,bindings,progress),confirmed);
        }
        public static Task<BatchStyleResult> ClearSelection(TargetSnapshot observed,string appRoot,
            Func<bool> isCurrent,IEnumerable<StyleBinding> bindings,Action<string> progress,
            Action<int,int,TargetSnapshot,bool> confirmed) {
            return BatchStyleWorkflow.Run(new NativeBatchSession(null,observed,appRoot,isCurrent,bindings,progress),confirmed);
        }
    }
}
