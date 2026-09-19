using System;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace RniPanel {
    public static partial class CaptureOneBridge {
        sealed class ViewerModeObservation {
            public bool Multiple;
            public bool Changed;
        }
        static void CheckViewerSelection(TargetSnapshot observed,TargetSnapshot connected,int count) {
            string reason=SafetyPolicy.CheckDocument(observed,connected);
            if(reason!=null)throw new InvalidOperationException(reason);
            if(!SafetyPolicy.IsAllowedCatalog(observed.DocumentPath))throw new InvalidOperationException("当前不是支持的图库，未改变查看器或照片。");
            if(observed.SelectedCount<1)throw new InvalidOperationException("请先在 C1 选择照片，未改变查看器或照片。");
            if(count>0&&observed.SelectedCount!=count)throw new InvalidOperationException("准备查看器期间选中数量改变，已停止。");
        }
        static ViewerModeObservation ObserveViewerMode(TargetSnapshot target,Func<bool> current,bool? requested) {
            RequireActiveWindow(target,current);
            AutomationElement viewMenu=null,customizeMenu=null;
            bool clicked=false;
            try {
                viewMenu=OpenBatchMenu(target,"viewToolStripMenuItem","查看","查看(V)","View");
                RequireStyleMenu(target,current);
                customizeMenu=OpenBatchMenu(target,viewMenu,"customizeViewerToolStripMenuItem","自定义查看器","Customize Viewer");
                RequireStyleMenu(target,current);
                var item=FindBatchControl(AutomationElement.FromHandle(target.Handle),customizeMenu,"viewerModeShowAllToolStripMenuItem","多视图","Multi View","Multi-view");
                bool actual=ReadNativeMenuChecked(item);
                LogNativeTiming("viewer-mode observed; multiple="+actual+"; requested="+(requested.HasValue?requested.Value.ToString():"read-only"));
                if(requested.HasValue&&actual!=requested.Value) {
                    RequireStyleMenu(target,current);
                    ClickNativeRow(item,()=>RequireStyleMenu(target,current));clicked=true;
                }
                return new ViewerModeObservation {Multiple=actual,Changed=clicked};
            } finally {
                // Let a real click finish and close its menus itself. Never
                // collapse between queued mouse-down and mouse-up.
                if(!clicked){CloseBatchMenu(customizeMenu);CloseBatchMenu(viewMenu);}
            }
        }
        public static async Task<TargetSnapshot> PreparePrimaryViewer(string appRoot,TargetSnapshot connected,Func<bool> current) {
            if(!current())throw new OperationCanceledException("连接已取消，未改变查看器。");
            // Normal single-photo and already-primary-view requests are exactly
            // the existing path: no menu lookup or viewer change is needed.
            try {
                var ready=await Task.Run(()=>InspectPrimary(appRoot));
                CheckViewerSelection(ready,connected,0);
                return ready;
            } catch(MultipleViewerException) {
                // Only this known layout condition authorizes preparation.
                // Database, identity, missing-control and timeout errors never
                // trigger an unrelated view toggle.
            }
            var selection=await Task.Run(()=>InspectSelection(appRoot));
            CheckViewerSelection(selection,connected,0);
            if(selection.SelectedCount<2)throw new InvalidOperationException("单选时存在多个查看器，可能是比较或参考视图；未自动切换。");
            bool changed=false;
            try {
                if(!current())throw new OperationCanceledException("连接已取消，未改变查看器。");
                ShowWindow(selection.Handle,3);
                if(!SetForegroundWindow(selection.Handle))throw new InvalidOperationException("无法激活 C1，未改变查看器。");
                await Task.Delay(150);
                var fresh=await Task.Run(()=>InspectSelection(appRoot));
                CheckViewerSelection(fresh,selection,selection.SelectedCount);
                var mode=await Task.Run(()=>ObserveViewerMode(selection,current,false));
                changed=mode.Changed;
                if(changed)await Task.Delay(150);
                var afterMode=await Task.Run(()=>ObserveViewerMode(selection,current,null));
                if(afterMode.Multiple)throw new InvalidOperationException("原生多视图未确认关闭，未开始应用或清除。");
                for(int attempt=0;attempt<5;attempt++) {
                    if(!current())throw new OperationCanceledException("连接已取消，未开始应用或清除。");
                    try {
                        var primary=await Task.Run(()=>InspectPrimary(appRoot));
                        CheckViewerSelection(primary,selection,selection.SelectedCount);
                        primary.RestoreMultiViewer=changed;
                        return primary;
                    } catch(MultipleViewerException) {
                        if(attempt==4)throw;
                    }
                    await Task.Delay(120);
                }
                throw new InvalidOperationException("查看器准备未完成。");
            } catch(Exception error) {
                if(changed)throw new InvalidOperationException(error.Message+" 查看器可能保持仅主图；本次未发送样式，也不会自动重试。",error);
                throw;
            }
        }
        static async Task RestorePrimaryViewer(TargetSnapshot original,string appRoot,Func<bool> current) {
            if(!original.RestoreMultiViewer)return;
            if(!current())throw new OperationCanceledException("连接已取消，查看器保持当前状态。");
            var selected=await Task.Run(()=>InspectSelection(appRoot));
            CheckViewerSelection(selected,original,original.SelectedCount);
            var mode=await Task.Run(()=>ObserveViewerMode(original,current,true));
            if(mode.Changed)await Task.Delay(150);
            var verified=await Task.Run(()=>ObserveViewerMode(original,current,null));
            if(!verified.Multiple)throw new InvalidOperationException("照片已逐张确认，但原多视图未确认恢复；不会再次切换。");
            selected=await Task.Run(()=>InspectSelection(appRoot));
            CheckViewerSelection(selected,original,original.SelectedCount);
            LogNativeTiming("viewer-mode original multi-view restored; selection="+original.SelectedCount);
        }
    }
}
