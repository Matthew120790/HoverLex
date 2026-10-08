using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace HoverLex
{
    public static partial class InputReader
    {
        internal static IntPtr SelectionFocusWindow()
        {
            IntPtr focus=FocusWindow(); if(focus==IntPtr.Zero) return GetForegroundWindow();
            IntPtr root=new IntPtr(RootWindow(focus)); string kind=ClassName(root),name=ProcessName(root);
            if(kind.StartsWith("Chrome_WidgetWin",StringComparison.Ordinal) || kind=="ConsoleWindowClass" || name.Equals("WindowsTerminal",StringComparison.OrdinalIgnoreCase) || SelectionWechat(root) || WhatsAppWindows.Host(root)!=IntPtr.Zero) return root;
            return focus;
        }
        public static InputSnapshot ReadSelection(long expectedFocus,int x,int y)
        {
            InputSnapshot empty=new InputSnapshot();
            try {
                IntPtr focus=SelectionFocusWindow();
                if(focus==IntPtr.Zero || focus.ToInt64()!=expectedFocus) return empty;
                long root=RootWindow(focus);
                InputSnapshot selected=ReadNativeSelection(focus) ?? ReadWordSelection(focus);
                if(selected==null) {
                    selected=SelectionFromElement(AutomationElement.FocusedElement,root);
                    if(selected==null) {
                        selected=ReadBrowserRendererSelection(root,x,y);
                    }
                    if(selected==null) {
                        AutomationElement at=AutomationElement.FromPoint(new System.Windows.Point(x,y));
                        selected=SelectionFromElement(at,root);
                    }
                    if(selected==null) {
                        AutomationElement window=AutomationElement.FromHandle(new IntPtr(root));
                        var candidates=window.FindAll(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty,true),new PropertyCondition(AutomationElement.IsOffscreenProperty,false)));
                        foreach(AutomationElement candidate in candidates) {
                            if(!candidate.Current.BoundingRectangle.Contains(new System.Windows.Point(x,y))) continue;
                            selected=SelectionFromElement(candidate,root); if(selected!=null) break;
                        }
                    }
                }
                if(SelectionFocusWindow()!=focus || selected==null) return empty;
                selected.FocusHandle=expectedFocus; selected.Mode="selection"; selected.Editable=false;
                selected.X=x; selected.Y=y; selected.Width=0; selected.Height=0;
                return selected;
            } catch { return new InputSnapshot { Error="无法读取选中文字，请在支持文本选择的窗口中重试" }; }
        }
        private static InputSnapshot ReadNativeSelection(IntPtr focus)
        {
            if(!IsNativeEdit(focus)) return null;
            IntPtr value;
            if((GetWindowLong(focus,-16)&0x20)!=0 || (SendSelection(focus,0x00d2,IntPtr.Zero,IntPtr.Zero,2,200,out value)!=IntPtr.Zero && value!=IntPtr.Zero))
                return new InputSnapshot { Error="密码输入框不翻译" };
            IntPtr range=Marshal.AllocHGlobal(8);
            try {
                Marshal.WriteInt32(range,0,0); Marshal.WriteInt32(range,4,0);
                bool rich=ClassName(focus).IndexOf("RichEdit",StringComparison.OrdinalIgnoreCase)>=0;
                if(SendSelection(focus,0x00b0u,range,IntPtr.Add(range,4),2,200,out value)==IntPtr.Zero)
                    return new InputSnapshot { Error="选中文字读取超时" };
                int start=Marshal.ReadInt32(range,0),end=Marshal.ReadInt32(range,4);
                if(start==end) return new InputSnapshot();
                if(start<0 || end<start || end-start>2000) return new InputSnapshot { Error="请每次选择不超过 2000 字的中文或英文" };
                if(SendSelection(focus,0x000e,IntPtr.Zero,IntPtr.Zero,2,200,out value)==IntPtr.Zero || value.ToInt64()>1000000)
                    return new InputSnapshot { Error="文档过长或暂时无法读取" };
                StringBuilder text=new StringBuilder((int)value.ToInt64()+1);
                if(ReadText(focus,0x000d,new IntPtr(text.Capacity),text,2,200,out value)==IntPtr.Zero) return new InputSnapshot();
                string document=rich ? text.ToString().Replace("\r\n","\r") : text.ToString();
                if(end>document.Length) return new InputSnapshot();
                return new InputSnapshot { Text=document.Substring(start,end-start),Id="selected-native:"+focus,Position=start+":"+end };
            } finally { Marshal.FreeHGlobal(range); }
        }
        private static InputSnapshot ReadWordSelection(IntPtr focus)
        {
            if(ClassName(focus)!="_WwG") return null;
            object window=null,selection=null,range=null;
            try {
                window=WordWindow(focus); if(window==null) return null;
                selection=ComGet(window,"Selection"); range=ComGet(selection,"Range");
                int start=Convert.ToInt32(ComGet(range,"Start")),end=Convert.ToInt32(ComGet(range,"End"));
                if(start==end) return new InputSnapshot();
                if(end-start>2000) return new InputSnapshot { Error="请每次选择不超过 2000 字的中文或英文" };
                return new InputSnapshot { Text=Convert.ToString(ComGet(range,"Text")).TrimEnd('\r','\a'),Id="selected-word:"+focus+":"+ComGet(range,"StoryType"),Position=start+":"+end };
            } finally { Release(range); Release(selection); Release(window); }
        }
        internal static InputSnapshot SelectionFromElement(AutomationElement element,long root)
        {
            List<AutomationElement> parents=new List<AutomationElement>(); bool inWindow=false;
            for(int depth=0;element!=null && depth<48;depth++) {
                if(element.Current.IsPassword) return new InputSnapshot { Error="密码输入框不翻译" };
                parents.Add(element);
                int handle=element.Current.NativeWindowHandle;
                if(handle!=0 && SelectionWindowMatches(new IntPtr(handle),root)) { inWindow=true; break; }
                element=TreeWalker.RawViewWalker.GetParent(element);
            }
            if(!inWindow) return null;
            foreach(AutomationElement candidate in parents) {
                try {
                object value;
                if(!candidate.TryGetCurrentPattern(TextPattern.Pattern,out value)) continue;
                TextPattern pattern=(TextPattern)value;
                var ranges=pattern.GetSelection(); StringBuilder text=new StringBuilder(),position=new StringBuilder();
                foreach(var range in ranges) {
                    string part=range.GetText(2001);
                    if(String.IsNullOrWhiteSpace(part)) continue;
                    if(text.Length>0) text.AppendLine(); text.Append(part);
                    position.Append(part.Length).Append(':');
                    // 仅核对选区坐标，不扫描终端的整份滚动历史。
                    try { foreach(var bounds in range.GetBoundingRectangles()) position.Append(bounds.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(';'); }
                    catch(NotSupportedException) { }
                    if(text.Length>2000) return new InputSnapshot { Error="请每次选择不超过 2000 字的中文或英文" };
                }
                if(text.Length>0) return new InputSnapshot { Text=text.ToString(),Id="selected-uia:"+String.Join(".",candidate.GetRuntimeId()),Position=position.ToString() };
                } catch { }
            }
            return null;
        }
    }

    internal sealed class SelectionShortcut
    {
        private readonly HashSet<int> controls=new HashSet<int>();
        public bool Candidate,Released;
        public long Revision,Gesture;
        public void Key(int key,bool down,bool otherModifier=false)
        {
            bool ctrl=key==0x11 || key==0xa2 || key==0xa3;
            if(ctrl) {
                if(down) {
                    if(controls.Count==0) { Revision++; Gesture++; Candidate=!otherModifier; Released=false; }
                    controls.Add(key);
                } else {
                    controls.Remove(key);
                    if(controls.Count==0 && Candidate) Released=true;
                }
            } else if(down) Invalidate();
        }
        public void Invalidate() { Revision++; Candidate=false; Released=false; }
        public bool CtrlDown { get { return controls.Count>0; } }
    }

    internal sealed class SelectionIntent
    {
        private long focus;
        public void Selected(long window) { focus=window; }
        public void Clear() { focus=0; }
        public bool Matches(long window) { return focus!=0 && focus==window; }
        public void Key(int key,bool ctrl,bool shift,bool otherModifier,long window)
        {
            if(key==0x11 || key==0xa2 || key==0xa3 || key==0x10 || key==0xa0 || key==0xa1) return;
            if(!otherModifier && ((ctrl && !shift && key==0x41) || (shift && key>=0x21 && key<=0x28))) { Selected(window); return; }
            // 复制保留原选区；输入、粘贴及光标移动会使记录失效。
            if(ctrl && !shift && !otherModifier && key==0x43 && Matches(window)) return;
            Clear();
        }
    }

    public sealed class SelectionTranslationController : IDisposable
    {
        private delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int type,Hook callback,IntPtr module,uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        private readonly Hook keyboard,mouse;
        private IntPtr keyHook,mouseHook;
        private readonly SelectionShortcut shortcut=new SelectionShortcut();
        private readonly SelectionIntent intent=new SelectionIntent();
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        private readonly TranslationPanel panel=new TranslationPanel();
        private readonly OnlineTranslator service=new OnlineTranslator();
        private readonly Action<string> status;
        private readonly Func<string,TranslationDirection,CancellationToken,Task<string>> translate;
        private CancellationTokenSource request;
        private InputSnapshot selection;
        private long seenRevision,seenGesture,gestureFocus;
        private Point gesturePoint;
        private bool enabled,disposed,reading,readComplete,started;
        private bool dragged,copyAttempted;
        private bool doubleClicked;
        private Point dragStart;
        private Point lastClickPoint;
        private long lastClick,lastClickFocus;
        internal TranslationPanel Panel { get { return panel; } }
        internal Action<string> Trace;
        internal Func<Point,long,Task> PointLookup;
        internal long GestureRevision { get { return shortcut.Revision; } }
        internal bool WaitForWhatsAppSelection { get { return enabled && shortcut.Candidate && intent.Matches(gestureFocus) && WhatsAppWindows.Host(new IntPtr(gestureFocus))!=IntPtr.Zero; } }
        public bool Enabled { get { return enabled; } set { if(enabled==value) return; enabled=value; Dismiss(); } }
        public bool EnglishCorrection { get { return service.CorrectionEnabled; } set { if(service.CorrectionEnabled==value) return; Dismiss(); service.CorrectionEnabled=value; } }
        public bool SuppressHover { get { return enabled && shortcut.Candidate && shortcut.Released && selection!=null && selection.Error.Length==0 && Eligible(selection.Text); } }
        public SelectionTranslationController(Action<string> message,Func<string,TranslationDirection,CancellationToken,Task<string>> translator=null)
        {
            status=message; translate=translator ?? service.TranslateAsync;
            keyboard=(code,messageId,data)=> {
                if(code>=0 && Marshal.ReadIntPtr(data,16)!=InputReader.OwnInput) {
                    int kind=messageId.ToInt32(),key=Marshal.ReadInt32(data);
                    if(kind==0x100 || kind==0x104 || kind==0x101 || kind==0x105) {
                        if(kind==0x100 || kind==0x104) intent.Key(key,shortcut.CtrlDown,Native.Down(0x10),Native.Down(0x12) || Native.Down(0x5b) || Native.Down(0x5c),InputReader.SelectionFocusWindow().ToInt64());
                        long old=shortcut.Gesture;
                        shortcut.Key(key,kind==0x100 || kind==0x104,Native.Down(0x10) || Native.Down(0x12) || Native.Down(0x5b) || Native.Down(0x5c));
                        if(old!=shortcut.Gesture) { gestureFocus=InputReader.SelectionFocusWindow().ToInt64(); gesturePoint=Cursor.Position; }
                        if(Trace!=null && (key==0x11 || key==0xa2 || key==0xa3)) Trace("Ctrl gesture="+shortcut.Gesture+" candidate="+shortcut.Candidate+" released="+shortcut.Released+" focus="+gestureFocus+" selected="+intent.Matches(gestureFocus));
                    }
                }
                return CallNextHookEx(keyHook,code,messageId,data);
            };
            mouse=(code,messageId,data)=> {
                if(code>=0) {
                    Point point=new Point(Marshal.ReadInt32(data),Marshal.ReadInt32(data,4));
                    if(messageId.ToInt32()==0x201) {
                        long now=Stopwatch.GetTimestamp();
                        doubleClicked=lastClick!=0 && (now-lastClick)*1000/Stopwatch.Frequency<=SystemInformation.DoubleClickTime &&
                            Math.Abs(point.X-lastClickPoint.X)<=SystemInformation.DoubleClickSize.Width/2 && Math.Abs(point.Y-lastClickPoint.Y)<=SystemInformation.DoubleClickSize.Height/2 &&
                            InputReader.SelectionFocusWindow().ToInt64()==lastClickFocus;
                        dragStart=point; dragged=false; intent.Clear();
                    }
                    if(messageId.ToInt32()==0x200 && Native.Down(1) && (Math.Abs(point.X-dragStart.X)>4 || Math.Abs(point.Y-dragStart.Y)>4)) dragged=true;
                    if(messageId.ToInt32()==0x202) {
                        long now=Stopwatch.GetTimestamp(); long focus=InputReader.SelectionFocusWindow().ToInt64();
                        if(dragged || doubleClicked) { intent.Selected(focus); if(Trace!=null) Trace("mouse selection focus="+focus); }
                        lastClick=dragged ? 0 : now; lastClickFocus=focus; lastClickPoint=point;
                    }
                }
                if(code>=0 && (messageId.ToInt32()==0x201 || messageId.ToInt32()==0x204 || messageId.ToInt32()==0x207 || messageId.ToInt32()==0x20a || messageId.ToInt32()==0x20e)) {
                    intent.Clear();
                    Point point=new Point(Marshal.ReadInt32(data),Marshal.ReadInt32(data,4));
                    if(!panel.IsShown || !panel.Bounds.Contains(point)) shortcut.Invalidate();
                }
                return CallNextHookEx(mouseHook,code,messageId,data);
            };
            keyHook=SetWindowsHookEx(13,keyboard,GetModuleHandle(null),0); mouseHook=SetWindowsHookEx(14,mouse,GetModuleHandle(null),0);
            if(keyHook==IntPtr.Zero || mouseHook==IntPtr.Zero) { Dispose(); throw new InvalidOperationException("无法监听 Ctrl，请重新启动 HoverLex"); }
            panel.Retry=()=> { if(enabled && selection!=null) Translate(selection,shortcut.Revision); };
            panel.Dismiss=Dismiss;
            timer.Interval=40; timer.Tick+=Tick; timer.Start();
        }
        public void Dismiss() { shortcut.Invalidate(); Reset(); }
        private void Reset()
        {
            if(request!=null) { request.Cancel(); request=null; }
            selection=null; readComplete=started=copyAttempted=false; panel.Hide(); seenRevision=shortcut.Revision;
        }
        private async void Tick(object sender,EventArgs e)
        {
            if(disposed || !enabled) return;
            if(seenRevision!=shortcut.Revision) Reset();
            if((reading || selection!=null || started) && InputReader.SelectionFocusWindow().ToInt64()!=gestureFocus) { Dismiss(); return; }
            if(!shortcut.Candidate) return;
            if(seenGesture!=shortcut.Gesture && !reading) {
                seenGesture=shortcut.Gesture; long revision=shortcut.Revision; reading=true;
                try {
                    InputSnapshot result=await ReadAsync(gestureFocus,gesturePoint,CancellationToken.None);
                    if(Trace!=null) Trace("direct reader length="+result.Text.Length+" error="+result.Error+" revision="+shortcut.Revision+" expected="+revision+" selected="+intent.Matches(gestureFocus)+" supported="+InputReader.SelectionCopySupported(new IntPtr(gestureFocus)));
                    if(disposed || !enabled || revision!=shortcut.Revision) return;
                    readComplete=true;
                    if(result.Error.Length>0) { selection=result; status(result.Error); return; }
                    if(Eligible(result.Text)) selection=result;
                } catch(Exception error) {
                    if(!disposed && enabled && revision==shortcut.Revision) { readComplete=true; status("选中文字读取失败："+error.Message); }
                } finally { reading=false; }
            }
            bool unreadable=selection==null || (selection.Error.Length>0 && !selection.Error.Contains("密码") && !selection.Error.Contains("2000"));
            if(!reading && readComplete && unreadable && !copyAttempted && shortcut.Released && intent.Matches(gestureFocus) && InputReader.SelectionCopySupported(new IntPtr(gestureFocus))) {
                copyAttempted=true; reading=true; long revision=shortcut.Revision;
                try {
                    InputSnapshot result=await ReadAsync(gestureFocus,gesturePoint,CancellationToken.None,true);
                    if(Trace!=null) Trace("compatibility reader length="+result.Text.Length+" error="+result.Error);
                    if(!disposed && enabled && revision==shortcut.Revision && Eligible(result.Text)) selection=result;
                    else if(!disposed && enabled && revision==shortcut.Revision) {
                        string tip=result.Error.Length>0 ? result.Error : "未读到选中文字，请重新拖选后轻按 Ctrl。";
                        panel.PendingSelection(new InputSnapshot { Text="选区读取未完成",Mode="selection",X=gesturePoint.X,Y=gesturePoint.Y });
                        panel.Failure(tip); panel.ShowNear(new InputSnapshot { X=gesturePoint.X,Y=gesturePoint.Y }); status(tip);
                    }
                } catch(Exception error) { if(!disposed) status("选区读取失败："+error.Message); }
                finally { reading=false; }
            }
            if(!started && readComplete && unreadable && shortcut.Released && !intent.Matches(gestureFocus) && PointLookup!=null &&
                (selection==null || !selection.Error.Contains("密码")) &&
                Math.Abs(Cursor.Position.X-gesturePoint.X)<=3 && Math.Abs(Cursor.Position.Y-gesturePoint.Y)<=3) {
                started=true; await PointLookup(gesturePoint,gestureFocus); return;
            }
            if(!started && readComplete && selection!=null && selection.Error.Length==0 && shortcut.Released) { started=true; Translate(selection,shortcut.Revision); }
        }
        internal static bool Eligible(string text) { return !String.IsNullOrWhiteSpace(text) && text.Length<=2000 && (InputTranslationGate.HasChinese(text) || Regex.IsMatch(text,"[A-Za-z]")); }
        internal static bool SameSelection(InputSnapshot a,InputSnapshot b) { return a!=null && b!=null && a.Text==b.Text && a.Id==b.Id && a.Position==b.Position && a.FocusHandle==b.FocusHandle && b.Error.Length==0; }
        private async void Translate(InputSnapshot source,long revision)
        {
            if(request!=null) request.Cancel();
            var owned=new CancellationTokenSource(); request=owned;
            TranslationDirection direction=OnlineTranslator.DirectionFor(source.Text);
            bool toChinese=direction==TranslationDirection.EnglishToChinese;
            panel.PendingSelection(source); panel.ShowNear(source); status(toChinese ? "正在将选中的英文翻译成中文…" : "正在将选中的中文翻译成英文…");
            try {
                bool copy=source.Id.StartsWith("selected-compat-copy:",StringComparison.Ordinal);
                var before=await ReadAsync(source.FocusHandle,gesturePoint,owned.Token,copy);
                if(!SameSelection(source,before)) { if(revision==shortcut.Revision) Dismiss(); return; }
                string result=await translate(source.Text,direction,owned.Token);
                var after=await ReadAsync(source.FocusHandle,gesturePoint,owned.Token,copy);
                if(owned.IsCancellationRequested || disposed || !enabled || revision!=shortcut.Revision || !panel.IsShown) return;
                if(!SameSelection(source,after)) { Dismiss(); return; }
                panel.SelectionResult(source,result); status(toChinese ? "英文句子已翻译 · 可复制译文" : "中文句子已翻译 · 可复制译文");
            } catch(OperationCanceledException) { }
            catch(Exception error) { if(!disposed && revision==shortcut.Revision && panel.IsShown) { panel.Failure("翻译失败："+error.Message); status("选中句子翻译失败，可点击重译"); } }
            finally { if(request==owned) request=null; owned.Dispose(); }
        }
        internal static Task<InputSnapshot> ReadAsync(long focus,Point point,CancellationToken token,bool copy=false)
        {
            return Task.Run(()=> {
                token.ThrowIfCancellationRequested();
                ProcessStartInfo info=new ProcessStartInfo(Application.ExecutablePath,"--selection-probe "+focus+" "+point.X+" "+point.Y+(copy ? " copy" : "")) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8 };
                using(Process process=Process.Start(info)) {
                    var output=process.StandardOutput.ReadToEndAsync(); var errors=process.StandardError.ReadToEndAsync();
                    using(token.Register(()=> { if(copy) return; try { if(!process.HasExited) process.Kill(); } catch { } })) {
                        if(!process.WaitForExit(copy ? 3500 : 2200)) { try { process.Kill(); } catch { } return new InputSnapshot { Error="选中文字读取超时，请再按一次 Ctrl" }; }
                        token.ThrowIfCancellationRequested();
                        string json=output.GetAwaiter().GetResult();
                        return String.IsNullOrWhiteSpace(json) ? new InputSnapshot { Error="当前窗口暂不支持读取选中文字" } : new JavaScriptSerializer().Deserialize<InputSnapshot>(json);
                    }
                }
            },token);
        }
        public void Dispose()
        {
            if(disposed) return; disposed=true; enabled=false;
            if(keyHook!=IntPtr.Zero) UnhookWindowsHookEx(keyHook); if(mouseHook!=IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            keyHook=mouseHook=IntPtr.Zero; Reset(); timer.Stop(); timer.Dispose(); panel.Dispose(); service.ClearCache();
        }
    }
}
