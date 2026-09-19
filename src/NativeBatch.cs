using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace RniPanel {
    public static partial class CaptureOneBridge {
        // This is a native-menu adapter, not a Capture One selection/style API.
        // EditMultiple is always observed live; user.config is not authoritative.
        static AutomationElement batchEditMode;
        static IntPtr batchEditHandle;
        static long batchEditProcessStart;

        static string MenuName(string name) {
            return (name??"").Split('\t')[0].Replace("&","").Replace("(_I)","").Replace("(I)","").Trim();
        }
        static AutomationElement[] MatchBatchControl(AutomationElement root,string id,HashSet<string> wanted) {
            var found=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,id));
            if(found!=null&&!found.Current.IsOffscreen)return new[]{found};
            return root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.MenuItem))
                .Cast<AutomationElement>().Where(e=>!e.Current.IsOffscreen&&wanted.Contains(MenuName(e.Current.Name))).ToArray();
        }
        static AutomationElement FindBatchControl(AutomationElement root,string id,params string[] names) {
            var wanted=new HashSet<string>(names.Select(MenuName),StringComparer.OrdinalIgnoreCase);
            var matches=MatchBatchControl(root,id,wanted);
            if(matches.Length==1)return matches[0];
            if(matches.Length==0) {
                // WinForms menu popups can be separate desktop children, not
                // descendants of the main HWND. Only inspect this process's
                // visible popup scopes after the main-window lookup misses.
                int process=root.Current.ProcessId,mainHandle=root.Current.NativeWindowHandle;
                var popupMatches=new List<AutomationElement>();
                foreach(AutomationElement popup in AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ProcessIdProperty,process))) {
                    if(popup.Current.NativeWindowHandle==mainHandle||popup.Current.IsOffscreen)continue;
                    popupMatches.AddRange(MatchBatchControl(popup,id,wanted));
                }
                if(popupMatches.Count==1)return popupMatches[0];
            }
            throw new InvalidOperationException("无法唯一找到 C1 原生命令 "+id+"；批量未继续。");
        }
        static AutomationElement OpenBatchMenu(TargetSnapshot target,string id,params string[] names) {
            var menu=FindBatchControl(AutomationElement.FromHandle(target.Handle),id,names);
            if(!menu.Current.IsEnabled)throw new InvalidOperationException("C1 原生菜单不可操作，批量已停止。");
            object pattern;
            if(menu.TryGetCurrentPattern(ExpandCollapsePattern.Pattern,out pattern))
                ((ExpandCollapsePattern)pattern).Expand();
            else if(menu.TryGetCurrentPattern(InvokePattern.Pattern,out pattern))
                ((InvokePattern)pattern).Invoke();
            else throw new InvalidOperationException("C1 原生菜单未提供可用展开动作，批量已停止。");
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
            throw new InvalidOperationException("C1 多图编辑开关没有公开可靠勾选状态，未开始批量发送。");
        }
        static void ChangeBatchEditMode(AutomationElement element) {
            object pattern;
            // A WinForms command menu can expose Toggle while Checked=true,
            // yet Toggle() need not execute its Click/Command handler. Invoke
            // the native command when available, then verify the real mode.
            if(element.TryGetCurrentPattern(InvokePattern.Pattern,out pattern))((InvokePattern)pattern).Invoke();
            else if(element.TryGetCurrentPattern(TogglePattern.Pattern,out pattern))((TogglePattern)pattern).Toggle();
            else throw new InvalidOperationException("不能操作 C1 原生多图编辑开关，批量已停止。");
        }
        static AutomationElement GetBatchEditMode(TargetSnapshot target) {
            if(batchEditHandle!=target.Handle||batchEditProcessStart!=target.ProcessStartTicks)batchEditMode=null;
            if(batchEditMode!=null&&batchEditHandle==target.Handle&&batchEditProcessStart==target.ProcessStartTicks) {
                try {ReadBatchEditMode(batchEditMode);return batchEditMode;}catch(ElementNotAvailableException) {batchEditMode=null;}
            }
            var root=AutomationElement.FromHandle(target.Handle);
            var toolbar=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"toolStripButtonEditMultiple"));
            if(toolbar!=null) {
                try {ReadBatchEditMode(toolbar);batchEditMode=toolbar;}catch(InvalidOperationException) {}
            }
            if(batchEditMode==null) {
                var menu=OpenBatchMenu(target,"imageToolStripMenuItem","图像","图像(I)","Image");
                try {
                    batchEditMode=FindBatchControl(AutomationElement.FromHandle(target.Handle),"editPrimaryOnlyToolStripMenuItem",
                        "编辑所有已选项","编辑所有已选变体","Edit All Selected Variants");
                    ReadBatchEditMode(batchEditMode);
                } finally {CloseBatchMenu(menu);}
            }
            batchEditHandle=target.Handle;batchEditProcessStart=target.ProcessStartTicks;
            return batchEditMode;
        }
        static void RequirePrimaryOnly(TargetSnapshot target) {
            if(ReadBatchEditMode(GetBatchEditMode(target)))
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
                    originalEditMultiple=ReadBatchEditMode(mode);modeCaptured=true;
                    if(originalEditMultiple)ChangeBatchEditMode(mode);
                });
                await Task.Delay(100);RequireWindow();
                await Task.Run(()=>RequirePrimaryOnly(connected));
                progress("批量：已确认仅编辑主图；正在逐张核对选区，不会向混合选区发送 toggle。");
            }
            async Task Navigate(bool first) {
                RequireWindow();await InspectPrimary();
                await Task.Run(()=> {
                    RequireWindow();RequirePrimaryOnly(connected);
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
                RequireWindow();await Task.Run(()=>RequirePrimaryOnly(expected));
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
                    bool actual=ReadBatchEditMode(mode);
                    if(actual!=originalEditMultiple)ChangeBatchEditMode(mode);
                });
                await Task.Delay(100);RequireWindow();
                if(await Task.Run(()=>ReadBatchEditMode(GetBatchEditMode(connected)))!=originalEditMultiple)
                    throw new InvalidOperationException("照片已逐张确认，但原编辑模式未确认恢复；请查看 C1。");
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
