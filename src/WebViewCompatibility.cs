using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace HoverLex
{
    // WebView2 的文字窗口可能不在宿主的子窗口树里，必须核对进程归属。
    internal static class WhatsAppWindows
    {
        private delegate bool Visit(IntPtr window,IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(Visit visit,IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window,Visit visit,IntPtr state);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint kind);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32First(IntPtr snapshot,ref ProcessEntry entry);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct ProcessEntry {
            public uint Size,Usage,Pid; public IntPtr Heap; public uint Module,Threads,Parent; public int Priority; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string Exe;
        }
        private static readonly object sync=new object();
        private static Dictionary<uint,ProcessEntry> processes=new Dictionary<uint,ProcessEntry>();
        private static long readAt;
        internal static uint Pid(IntPtr window) { uint pid; GetWindowThreadProcessId(window,out pid); return pid; }
        internal static uint HostPid(uint pid)
        {
            lock(sync) {
                if(readAt==0 || Stopwatch.GetTimestamp()-readAt>Stopwatch.Frequency) {
                    var next=new Dictionary<uint,ProcessEntry>(); IntPtr snapshot=CreateToolhelp32Snapshot(2,0);
                    if(snapshot==new IntPtr(-1)) return 0;
                    try { ProcessEntry entry=new ProcessEntry { Size=(uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                        if(Process32First(snapshot,ref entry)) do { next[entry.Pid]=entry; } while(Process32Next(snapshot,ref entry));
                    } finally { CloseHandle(snapshot); }
                    processes=next; readAt=Stopwatch.GetTimestamp();
                }
                for(int depth=0;pid!=0 && depth<8;depth++) {
                    ProcessEntry entry; if(!processes.TryGetValue(pid,out entry)) return 0;
                    if(String.Equals(entry.Exe,"WhatsApp.Root.exe",StringComparison.OrdinalIgnoreCase) || String.Equals(entry.Exe,"WhatsApp.exe",StringComparison.OrdinalIgnoreCase)) return pid;
                    if(!String.Equals(entry.Exe,"msedgewebview2.exe",StringComparison.OrdinalIgnoreCase)) return 0;
                    pid=entry.Parent;
                }
                return 0;
            }
        }
        internal static IntPtr Host(IntPtr window)
        {
            if(window==IntPtr.Zero) return IntPtr.Zero;
            uint host=HostPid(Pid(window)); if(host==0) return IntPtr.Zero;
            IntPtr root=GetAncestor(window,2);
            if(Pid(root)==host) return root;
            IntPtr found=IntPtr.Zero; int matches=0;
            EnumWindows((candidate,state)=> {
                if(Pid(candidate)==host && IsWindowVisible(candidate) && ScreenReader.WindowClass(candidate)=="WinUIDesktopWin32WindowClass") { found=candidate; matches++; }
                return true;
            },IntPtr.Zero);
            return matches==1 ? found : IntPtr.Zero;
        }
        internal static bool Belongs(IntPtr window,IntPtr host) { return host!=IntPtr.Zero && Host(window)==host; }
        internal static List<IntPtr> Renderers(IntPtr host)
        {
            var result=new List<IntPtr>();
            if(host==IntPtr.Zero || Host(host)!=host) return result;
            Visit child=(window,state)=> { if(ScreenReader.WindowClass(window)=="Chrome_RenderWidgetHostHWND" && IsWindowVisible(window) && Belongs(window,host) && !result.Contains(window)) result.Add(window); return true; };
            EnumChildWindows(host,child,IntPtr.Zero);
            EnumWindows((window,state)=> { if(ScreenReader.WindowClass(window).StartsWith("Chrome_WidgetWin",StringComparison.Ordinal) && IsWindowVisible(window) && Belongs(window,host)) EnumChildWindows(window,child,IntPtr.Zero); return true; },IntPtr.Zero);
            return result;
        }
        internal static AutomationElement FocusedEditor(IntPtr host)
        {
            foreach(IntPtr renderer in Renderers(host)) try {
                var root=AutomationElement.FromHandle(renderer);
                var nodes=root.FindAll(TreeScope.Subtree,new PropertyCondition(AutomationElement.HasKeyboardFocusProperty,true));
                foreach(AutomationElement node in nodes) try {
                    if(node.Current.IsPassword) return node;
                    if(node.Current.ControlType==ControlType.Edit && node.Current.IsEnabled && !node.Current.IsOffscreen) return node;
                } catch { }
            } catch { }
            return null;
        }
    }

    public static partial class InputReader
    {
        private static InputSnapshot ReadWhatsAppFocused(IntPtr focus)
        {
            IntPtr host=WhatsAppWindows.Host(focus); if(host==IntPtr.Zero) return null;
            AutomationElement editor=WhatsAppWindows.FocusedEditor(host);
            return editor==null ? new InputSnapshot() : ReadWhatsAppEditor(editor,focus);
        }
        internal static InputSnapshot ReadWhatsAppEditor(AutomationElement editor,IntPtr focus)
        {
            if(editor.Current.IsPassword) return new InputSnapshot { Error="密码输入框不翻译" };
            if(!editor.Current.HasKeyboardFocus || !editor.Current.IsEnabled || editor.Current.IsOffscreen || editor.Current.ControlType!=ControlType.Edit) return new InputSnapshot();
            object pattern;
            if(!editor.TryGetCurrentPattern(ValuePattern.Pattern,out pattern)) return new InputSnapshot();
            ValuePattern value=(ValuePattern)pattern;
            if(value.Current.IsReadOnly) return new InputSnapshot();
            string contents=value.Current.Value ?? "";
            if(contents.Length>2000) return new InputSnapshot { Error="当前输入超过 2000 字，请分段翻译" };
            var result=new InputSnapshot { Id="whatsapp:"+String.Join(".",editor.GetRuntimeId()),FocusHandle=focus.ToInt64(),Text=contents,Mode="whatsapp-value",Editable=true };
            // Value 是编辑框的真实内容；段落展开可能越过 WebView 编辑框边界。
            if(editor.TryGetCurrentPattern(TextPattern.Pattern,out pattern)) {
                TextPattern text=(TextPattern)pattern; TextPatternRange document=text.DocumentRange;
                if(document.GetText(2001)==contents) result.Range=document;
                var selection=text.GetSelection();
                if(selection.Length==1 && selection[0].CompareEndpoints(TextPatternRangeEndpoint.Start,document,TextPatternRangeEndpoint.Start)>=0 && selection[0].CompareEndpoints(TextPatternRangeEndpoint.End,document,TextPatternRangeEndpoint.End)<=0) {
                    var before=document.Clone(); before.MoveEndpointByRange(TextPatternRangeEndpoint.End,selection[0],TextPatternRangeEndpoint.Start);
                    result.Position=before.GetText(2001).Length+":"+selection[0].GetText(2001).Length;
                } else result.Position="unavailable";
            }
            var bounds=editor.Current.BoundingRectangle;
            if(!bounds.IsEmpty) { result.X=(int)bounds.X; result.Y=(int)bounds.Y; result.Width=(int)bounds.Width; result.Height=(int)bounds.Height; }
            return result;
        }
        internal static bool SelectionWindowMatches(IntPtr window,long root)
        {
            return RootWindow(window)==root || WhatsAppWindows.Belongs(window,new IntPtr(root));
        }
        private static string ReplaceWhatsAppValue(InputSnapshot expected,string english,InputProbeGuard guard)
        {
            IntPtr focus=FocusWindow(); AutomationElement editor=WhatsAppWindows.FocusedEditor(focus);
            if(editor==null || !SameSource(expected,ReadWhatsAppEditor(editor,focus)) || guard.Changed) return "WhatsApp 输入框或原文已变化，未替换";
            object pattern;
            if(!editor.TryGetCurrentPattern(ValuePattern.Pattern,out pattern)) return "此输入框不支持安全替换，请使用复制";
            var value=(ValuePattern)pattern;
            english=english.Replace("\r"," ").Replace("\n"," ").Replace("\t"," ");
            Application.DoEvents();
            if(guard.Changed || FocusWindow()!=focus || !editor.Current.HasKeyboardFocus || value.Current.IsReadOnly || value.Current.Value!=expected.Text) return "WhatsApp 输入或焦点已变化，未替换";
            // 写入经过核对的整框内容，不依赖 WebView 的跨窗口选区，也不发送按键。
            value.SetValue(english);
            if(value.Current.Value!=english) return "WhatsApp 未确认替换结果，请检查输入框";
            if(!guard.Changed && FocusWindow()==focus) editor.SetFocus();
            return "";
        }
    }

    public static partial class ScreenReader
    {
        private static CaptureResult ReadWhatsAppPoint(int x,int y,out bool password)
        {
            password=false; IntPtr host=WhatsAppWindows.Host(WindowFromPoint(new Point(x,y)));
            if(host==IntPtr.Zero) return null;
            var point=new System.Windows.Point(x,y);
            foreach(IntPtr renderer in WhatsAppWindows.Renderers(host)) try {
                var root=AutomationElement.FromHandle(renderer);
                if(!root.Current.BoundingRectangle.Contains(point)) continue;
                var protectedNodes=root.FindAll(TreeScope.Subtree,new PropertyCondition(AutomationElement.IsPasswordProperty,true));
                foreach(AutomationElement protectedNode in protectedNodes) if(protectedNode.Current.BoundingRectangle.Contains(point)) { password=true; return null; }
                var nodes=root.FindAll(TreeScope.Subtree,new AndCondition(new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty,true),new PropertyCondition(AutomationElement.IsOffscreenProperty,false)));
                foreach(AutomationElement node in nodes) try {
                    if(!node.Current.BoundingRectangle.Contains(point)) continue;
                    AutomationElement ancestor=node;
                    for(int depth=0;ancestor!=null && depth<48;depth++) {
                        if(ancestor.Current.IsPassword) { password=true; return null; }
                        if(ancestor.Current.NativeWindowHandle==renderer.ToInt64()) break;
                        ancestor=TreeWalker.RawViewWalker.GetParent(ancestor);
                    }
                    object pattern;
                    if(node.TryGetCurrentPattern(TextPattern.Pattern,out pattern)) { var found=ReadText((TextPattern)pattern,x,y); if(found!=null) { found.Method="WhatsApp 直接取词"; return found; } }
                } catch { }
            } catch { }
            return null;
        }
    }
}
