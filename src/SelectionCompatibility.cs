using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace HoverLex
{
    public static partial class InputReader
    {
        internal static Action<string> SelectionReadTrace;
        private delegate bool SelectionChildWindow(IntPtr window,IntPtr state);
        [DllImport("user32.dll",EntryPoint="EnumChildWindows")] private static extern bool SelectionEnumChildren(IntPtr window,SelectionChildWindow callback,IntPtr state);
        internal static InputSnapshot ReadBrowserRendererSelection(long root,int x,int y)
        {
            InputSnapshot result=null;
            IntPtr host=WhatsAppWindows.Host(new IntPtr(root));
            if(host!=IntPtr.Zero) {
                foreach(IntPtr child in WhatsAppWindows.Renderers(host)) try {
                    var nodes=AutomationElement.FromHandle(child).FindAll(TreeScope.Subtree,new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty,true));
                    foreach(AutomationElement node in nodes) try { result=SelectionFromElement(node,root); if(result!=null) return result; } catch { }
                } catch { }
                return null;
            }
            if(!SelectionBrowser(new IntPtr(root))) return null;
            SelectionEnumChildren(new IntPtr(root),(child,state)=> {
                if(ClassName(child)!="Chrome_RenderWidgetHostHWND" || !IsWindowVisible(child)) return true;
                AutomationElement renderer=AutomationElement.FromHandle(child);
                var nodes=renderer.FindAll(TreeScope.Subtree,new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty,true));
                foreach(AutomationElement node in nodes) try {
                    result=SelectionFromElement(node,root); if(result!=null) return false;
                } catch { }
                return true;
            },IntPtr.Zero);
            return result;
        }
        internal static bool SelectionBrowser(IntPtr focus)
        {
            string name=ProcessName(new IntPtr(RootWindow(focus)));
            return name.Equals("chrome",StringComparison.OrdinalIgnoreCase) || name.Equals("msedge",StringComparison.OrdinalIgnoreCase) || name.Equals("firefox",StringComparison.OrdinalIgnoreCase);
        }
        internal static bool SelectionWechat(IntPtr focus)
        {
            IntPtr root=new IntPtr(RootWindow(focus)); string name=ProcessName(root),kind=ClassName(root);
            return (name.Equals("Weixin",StringComparison.OrdinalIgnoreCase) || name.Equals("WeChat",StringComparison.OrdinalIgnoreCase)) && (kind=="Qt51514QWindowIcon" || kind=="WeChatMainWndForPC" || kind=="mmui::MainWindow");
        }
        internal static bool SelectionCopySupported(IntPtr focus) { return SelectionBrowser(focus) || SelectionWechat(focus) || WhatsAppWindows.Host(focus)!=IntPtr.Zero; }
        internal static bool SelectionClipboardOwnerMatches(IntPtr focus,uint ownerPid)
        {
            uint targetPid=WhatsAppWindows.Pid(new IntPtr(RootWindow(focus)));
            if(ownerPid==0 || targetPid==0) return false;
            if(ownerPid==targetPid) return true;
            IntPtr host=WhatsAppWindows.Host(focus);
            return host!=IntPtr.Zero && WhatsAppWindows.HostPid(ownerPid)==WhatsAppWindows.Pid(host);
        }
        internal static InputSnapshot CopyCompatibilitySelection(long expectedFocus,int x,int y)
        {
            return CopySelectionCore(expectedFocus,x,y,false);
        }
        internal static InputSnapshot CopySelectionFixture(long expectedFocus,int x,int y)
        {
            uint pid; GetWindowThreadProcessId(new IntPtr(RootWindow(new IntPtr(expectedFocus))),out pid);
            if(pid!=Process.GetCurrentProcess().Id) return new InputSnapshot();
            return CopySelectionCore(expectedFocus,x,y,true);
        }
        private static InputSnapshot CopySelectionCore(long expectedFocus,int x,int y,bool fixture)
        {
            IntPtr focus=SelectionFocusWindow(); InputSnapshot result=new InputSnapshot();
            if(focus==IntPtr.Zero || focus.ToInt64()!=expectedFocus || (!fixture && !SelectionCopySupported(focus)) || ModifiersDown()) { if(SelectionReadTrace!=null) SelectionReadTrace("copy initial guard rejected"); return result; }
            // 只复制现有选区，不改变选区、不粘贴、不发送消息。终端不走此路径。
            try {
                if(!fixture && SelectionWechat(focus) && !WechatMainCanvas(focus)) { if(SelectionReadTrace!=null) SelectionReadTrace("copy main canvas guard rejected"); return result; }
                IntPtr host=WhatsAppWindows.Host(focus);
                if(host!=IntPtr.Zero) foreach(IntPtr renderer in WhatsAppWindows.Renderers(host)) {
                    var protectedNodes=AutomationElement.FromHandle(renderer).FindAll(TreeScope.Subtree,new PropertyCondition(AutomationElement.IsPasswordProperty,true));
                    foreach(AutomationElement node in protectedNodes)
                        if(node.Current.HasKeyboardFocus || node.Current.BoundingRectangle.Contains(new System.Windows.Point(x,y))) return new InputSnapshot { Error="密码输入框不翻译" };
                }
                AutomationElement[] starts={ AutomationElement.FocusedElement,AutomationElement.FromPoint(new System.Windows.Point(x,y)) };
                foreach(AutomationElement start in starts) {
                    AutomationElement parent=start;
                    for(int depth=0;parent!=null && depth<48;depth++) {
                        if(parent.Current.IsPassword) return new InputSnapshot { Error="密码输入框不翻译" };
                        if(parent.Current.NativeWindowHandle==RootWindow(focus)) break;
                        parent=TreeWalker.RawViewWalker.GetParent(parent);
                    }
                }
            } catch { return new InputSnapshot { Error="无法确认选区安全，未读取内容" }; }
            DataObject saved=null; uint copied=0;
            using(InputProbeGuard guard=new InputProbeGuard()) try {
                saved=CopyClipboard(); uint sequence=GetClipboardSequenceNumber();
                if(guard.Changed || SelectionFocusWindow()!=focus || ModifiersDown()) return result;
                SendKeysBatch(new int[] { 0x11,0x43 },false);
                Stopwatch wait=Stopwatch.StartNew();
                while(GetClipboardSequenceNumber()==sequence && wait.ElapsedMilliseconds<450 && !guard.Changed && SelectionFocusWindow()==focus) { Application.DoEvents(); Thread.Sleep(10); }
                copied=GetClipboardSequenceNumber();
                if(copied==sequence) { copied=0; if(SelectionReadTrace!=null) SelectionReadTrace("copy did not change clipboard"); return result; }
                uint ownerPid; GetWindowThreadProcessId(GetClipboardOwner(),out ownerPid);
                if(!SelectionClipboardOwnerMatches(focus,ownerPid)) { copied=0; if(SelectionReadTrace!=null) SelectionReadTrace("copy owner is outside the source app"); return result; }
                if(guard.Changed || SelectionFocusWindow()!=focus) return result;
                string text=Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : "";
                if(text.Length>2000) return new InputSnapshot { Error="请每次选择不超过 2000 字的中文或英文" };
                result=new InputSnapshot { Text=text,Id="selected-compat-copy:"+RootWindow(focus)+":"+focus,Position=text,Mode="selection",FocusHandle=expectedFocus,X=x,Y=y };
                return result;
            } catch { result.Error="选区兼容读取未完成，请重新选择后再按 Ctrl"; return result; }
            finally {
                if(saved!=null && copied!=0 && !guard.ClipboardAction && GetClipboardSequenceNumber()==copied) try { if(saved.GetFormats(false).Length==0) Clipboard.Clear(); else Clipboard.SetDataObject(saved,true); }
                catch { result.Text=""; result.Error="剪贴板恢复失败，请检查剪贴板"; }
            }
        }
    }
}
