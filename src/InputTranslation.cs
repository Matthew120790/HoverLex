using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace HoverLex
{
    public sealed class InputSnapshot
    {
        public string Id = "", Text = "", Mode = "", Error = "", Position = "";
        public bool Editable, Composing, Debounced, RequirePointer;
        public long FocusHandle, InputSerial;
        public int X, Y, Width, Height;
        public int PointerX,PointerY;
        [ScriptIgnore] internal TextPatternRange Range;
    }

    public sealed class InputTranslationGate
    {
        public const int DebounceMilliseconds=450;
        private string id = "", text = "", position="";
        private long activity, changedAt;
        private bool pending, asked, dirty, suspended;
        public int Generation { get; private set; }
        public InputSnapshot Source { get; private set; }
        public void Reset(long inputActivity=0) { Generation++; id=""; text=""; position=""; activity=inputActivity; pending=asked=dirty=suspended=false; Source=null; }
        internal void Complete(int generation) { if(generation==Generation) pending=false; }
        public static bool HasChinese(string value) { return Regex.IsMatch(value ?? "",@"[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]"); }
        public static bool HasInput(string value) { return HasChinese(value) || Regex.IsMatch(EnglishProofreader.Mask(value ?? ""),"[A-Za-z]"); }
        public bool Observe(InputSnapshot current, long inputActivity, long now, bool enabled,long inputFocus=0,bool englishCorrection=true)
        {
            if(!enabled) { if(id.Length>0 || pending) Reset(activity); return false; }
            if(current==null || (!current.Editable && !current.Composing)) {
                // 重绘会短暂隐藏输入范围，保留尚未提交的输入，但取消旧请求。
                if(id.Length>0 && !suspended) { Generation++; suspended=true; asked=false; Source=null; }
                return false;
            }
            suspended=false;
            if(id!=current.Id) {
                bool edited=inputActivity!=activity && inputFocus!=0 && inputFocus==current.FocusHandle;
                bool typed=edited && (HasChinese(current.Text) || (englishCorrection && HasInput(current.Text)));
                Generation++; id=current.Id; text=current.Text; position=current.Position; dirty=edited || current.Composing; if(!current.Composing) activity=inputActivity;
                changedAt=current.Debounced ? now-700 : now; pending=typed && !current.Composing; asked=false; Source=pending ? current : null; return false;
            }
            if(inputActivity!=activity) { Generation++; activity=inputActivity; dirty=inputFocus==0 || inputFocus==current.FocusHandle; changedAt=now; pending=asked=false; Source=null; }
            if(current.Composing) { if(asked) Generation++; dirty=true; asked=false; Source=null; return false; }
            if(text!=current.Text) {
                Generation++; text=current.Text; pending=dirty && (HasChinese(text) || (englishCorrection && HasInput(text))) && !String.IsNullOrWhiteSpace(text);
                dirty=false; activity=inputActivity; changedAt=current.Debounced ? now-DebounceMilliseconds : now; asked=false; Source=pending ? current : null;
            } else if(position!=current.Position && !dirty) {
                Generation++; pending=asked=false; Source=null;
            }
            position=current.Position;
            if(pending && !asked && now-changedAt>=DebounceMilliseconds) { asked=true; Source=current; Source.InputSerial=inputActivity; return true; }
            return false;
        }
    }

    // 只记录输入动作的计数，不记录键入内容；关闭功能时移除钩子。
    public sealed class InputActivity : IDisposable
    {
        private delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
        [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int type,Hook hook,IntPtr module,uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        private readonly Hook callback;
        private readonly Hook mouse;
        private IntPtr handle,mouseHandle;
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        private static readonly uint ownPid=(uint)Process.GetCurrentProcess().Id;
        private long serial;
        private long revision;
        private bool tabConsumed;
        public Func<bool> CanReplaceWithTab;
        public Action ReplaceWithTab;
        public long Serial { get { return Interlocked.Read(ref serial); } }
        public long Revision { get { return Interlocked.Read(ref revision); } }
        public long LastInputAt { get; private set; }
        public long LastFocusHandle { get; private set; }
        public InputActivity()
        {
            callback=OnKey; handle=SetWindowsHookEx(13,callback,GetModuleHandle(null),0);
            if(handle==IntPtr.Zero) throw new InvalidOperationException("无法监听输入动作，请重新开启输入翻译");
            mouse=(code,message,data)=> { if(code>=0 && (message.ToInt32()==0x201 || message.ToInt32()==0x204 || message.ToInt32()==0x207 || message.ToInt32()==0x20a)) {
                Point point=new Point(Marshal.ReadInt32(data),Marshal.ReadInt32(data,4));
                uint pid; GetWindowThreadProcessId(WindowFromPoint(point),out pid);
                if(pid!=ownPid) Interlocked.Increment(ref revision);
            } return CallNextHookEx(mouseHandle,code,message,data); };
            mouseHandle=SetWindowsHookEx(14,mouse,GetModuleHandle(null),0);
            if(mouseHandle==IntPtr.Zero) { Dispose(); throw new InvalidOperationException("无法核对鼠标焦点，请重新开启输入翻译"); }
        }
        private IntPtr OnKey(int code,IntPtr message,IntPtr data)
        {
            if(code>=0 && Marshal.ReadInt32(data)==9 && Marshal.ReadIntPtr(data,16)!=InputReader.OwnInput) {
                int kind=message.ToInt32();
                if((kind==0x100 || kind==0x104) && (tabConsumed || (!InputReader.ModifiersDown() && CanReplaceWithTab!=null && CanReplaceWithTab()))) { tabConsumed=true; return new IntPtr(1); }
                if((kind==0x101 || kind==0x105) && tabConsumed) { tabConsumed=false; if(ReplaceWithTab!=null) ReplaceWithTab(); return new IntPtr(1); }
            }
            if(code>=0 && (message.ToInt32()==0x100 || message.ToInt32()==0x104) && Marshal.ReadIntPtr(data,16)!=InputReader.OwnInput) {
                int key=Marshal.ReadInt32(data);
                Interlocked.Increment(ref revision);
                if((Native.Down(0x11) && key!=0x56 && key!=8) || Native.Down(0x12) || Native.Down(0x5b) || Native.Down(0x5c)) return CallNextHookEx(handle,code,message,data);
                if(key==8 || key==13 || key==32 || key==46 || key==229 || key==231 || (key>=0x30 && key<=0x5a) || (key>=0x60 && key<=0x6f) || (key>=0xba && key<=0xe2)) {
                    Interlocked.Increment(ref serial); LastInputAt=Stopwatch.GetTimestamp(); LastFocusHandle=InputReader.FocusWindow().ToInt64();
                }
            }
            return CallNextHookEx(handle,code,message,data);
        }
        public void Dispose() { if(handle!=IntPtr.Zero) { UnhookWindowsHookEx(handle); handle=IntPtr.Zero; } if(mouseHandle!=IntPtr.Zero) { UnhookWindowsHookEx(mouseHandle); mouseHandle=IntPtr.Zero; } }
    }

    public static partial class InputReader
    {
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo { public int Size,Flags; public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret; public Rectangle CaretBounds; }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder name,int count);
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr window,int index);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageTimeoutW")] private static extern IntPtr ReadText(IntPtr window,uint message,IntPtr count,StringBuilder text,uint flags,uint timeout,out IntPtr result);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageTimeoutW",SetLastError=true)] private static extern IntPtr SendText(IntPtr window,uint message,IntPtr value,string text,uint flags,uint timeout,out IntPtr result);
        [DllImport("imm32.dll")] private static extern IntPtr ImmGetContext(IntPtr window);
        [DllImport("imm32.dll")] private static extern bool ImmReleaseContext(IntPtr window,IntPtr context);
        [DllImport("imm32.dll",CharSet=CharSet.Unicode)] private static extern int ImmGetCompositionStringW(IntPtr context,uint index,IntPtr buffer,uint length);
        [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort Key,Scan; public uint Flags,Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyInput Key; [FieldOffset(0)] public MouseInput Mouse; }
        [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X,Y; public uint Data,Flags,Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Value; }
        [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,Input[] input,int size);
        internal static IntPtr FocusWindow()
        {
            IntPtr foreground=GetForegroundWindow(); uint process; uint thread=GetWindowThreadProcessId(foreground,out process);
            IntPtr whatsapp=WhatsAppWindows.Host(foreground); if(whatsapp!=IntPtr.Zero) return whatsapp;
            GuiThreadInfo info=new GuiThreadInfo { Size=Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if(GetGUIThreadInfo(thread,ref info) && info.Focus!=IntPtr.Zero) return info.Focus;
            string name=ProcessName(foreground);
            return ClassName(foreground)=="ConsoleWindowClass" || name.Equals("WindowsTerminal",StringComparison.OrdinalIgnoreCase) || name.Equals("Weixin",StringComparison.OrdinalIgnoreCase) || name.Equals("WeChat",StringComparison.OrdinalIgnoreCase) ? foreground : IntPtr.Zero;
        }
        public static InputSnapshot ReadFocused()
        {
            try {
                IntPtr focus=FocusWindow(); InputSnapshot native=ReadNativeEdit(focus);
                bool composing=CompositionUiVisible();
                if(native!=null) { native.Composing|=composing; return native; }
                InputSnapshot word=ReadWord(focus); if(word!=null) { word.Composing|=composing; return word; }
                InputSnapshot whatsapp=ReadWhatsAppFocused(focus); if(whatsapp!=null) { whatsapp.Composing|=composing; return whatsapp; }
                InputSnapshot result=ReadElement(AutomationElement.FocusedElement,focus);
                if(result.Editable) result.Composing|=composing;
                return result.Editable || result.Composing ? result : ReadWechatCached(focus) ?? result;
            }
            catch { return new InputSnapshot { Error="当前输入框暂时无法读取，可在翻译练习中测试" }; }
        }
        private static bool IsNativeEdit(IntPtr window)
        {
            StringBuilder name=new StringBuilder(256); GetClassName(window,name,256); string kind=name.ToString();
            return kind.Equals("Edit",StringComparison.OrdinalIgnoreCase) || kind.StartsWith("RichEdit",StringComparison.OrdinalIgnoreCase) ||
                kind.IndexOf(".EDIT.",StringComparison.OrdinalIgnoreCase)>=0 || kind.IndexOf(".RichEdit",StringComparison.OrdinalIgnoreCase)>=0;
        }
        private static InputSnapshot ReadNativeEdit(IntPtr focus)
        {
            if(focus==IntPtr.Zero || !IsNativeEdit(focus)) return null;
            int style=GetWindowLong(focus,-16); IntPtr value;
            if((style&0x20)!=0 || (SendSelection(focus,0x00d2,IntPtr.Zero,IntPtr.Zero,2,300,out value)!=IntPtr.Zero && value!=IntPtr.Zero))
                return new InputSnapshot { Error="密码输入框不翻译" };
            if((style&0x800)!=0 || !IsWindowEnabled(focus) || !IsWindowVisible(focus)) return new InputSnapshot();
            uint process; GetWindowThreadProcessId(focus,out process);
            InputSnapshot result=new InputSnapshot { Id="native:"+process+":"+focus.ToInt64(),FocusHandle=focus.ToInt64(),Mode="value" };
            IntPtr context=ImmGetContext(focus);
            if(context!=IntPtr.Zero) try { if(ImmGetCompositionStringW(context,8,IntPtr.Zero,0)>0) { result.Composing=true; return result; } } finally { ImmReleaseContext(focus,context); }
            if(SendSelection(focus,0x000e,IntPtr.Zero,IntPtr.Zero,2,300,out value)==IntPtr.Zero) return new InputSnapshot { Error="输入框读取超时" };
            if(value.ToInt64()>2000) return new InputSnapshot { Error="当前输入超过 2000 字，请分段翻译" };
            StringBuilder text=new StringBuilder(2001);
            if(ReadText(focus,0x000d,new IntPtr(text.Capacity),text,2,300,out value)==IntPtr.Zero) return new InputSnapshot { Error="输入框读取超时" };
            result.Text=text.ToString(); result.Editable=true; NativeRect bounds;
            if(SendSelection(focus,0x00b0,IntPtr.Zero,IntPtr.Zero,2,300,out value)!=IntPtr.Zero) result.Position=value.ToInt64().ToString();
            if(GetWindowRect(focus,out bounds)) { result.X=bounds.Left; result.Y=bounds.Top; result.Width=bounds.Right-bounds.Left; result.Height=bounds.Bottom-bounds.Top; }
            return result;
        }
        internal static InputSnapshot ReadElement(AutomationElement element,IntPtr focus)
        {
            InputSnapshot result=new InputSnapshot();
            if(element==null) return result;
            // 无法确认密码标记时直接停止，不猜测、不截图输入内容。
            AutomationElement parent=element;
            for(int depth=0;parent!=null && depth<12;depth++) {
                if(parent.Current.IsPassword) return new InputSnapshot { Error="密码输入框不翻译" };
                parent=TreeWalker.ControlViewWalker.GetParent(parent);
            }
            AutomationElement original=element;
            if(!element.Current.IsEnabled || element.Current.IsOffscreen) return result;
            // 某些编辑器把焦点放在文字子节点，沿父节点找实际编辑器。
            for(int depth=0;depth<5;depth++) {
                object candidate; ControlType kind=element.Current.ControlType;
                if((element.Current.HasKeyboardFocus || original.Current.HasKeyboardFocus) &&
                    (element.TryGetCurrentPattern(ValuePattern.Pattern,out candidate) || element.TryGetCurrentPattern(TextPattern.Pattern,out candidate))) break;
                element=TreeWalker.RawViewWalker.GetParent(element);
                if(element==null || element.Current.ControlType==ControlType.Window || element.Current.IsPassword) return result;
            }
            result.Id=String.Join(".",Array.ConvertAll(element.GetRuntimeId(),n=>n.ToString())); result.FocusHandle=focus.ToInt64();
            IntPtr context=focus==IntPtr.Zero ? IntPtr.Zero : ImmGetContext(focus);
            if(context!=IntPtr.Zero) try {
                if(ImmGetCompositionStringW(context,8,IntPtr.Zero,0)>0) { result.Composing=true; return result; }
            } finally { ImmReleaseContext(focus,context); }
            object pattern;
            if(element.TryGetCurrentPattern(ValuePattern.Pattern,out pattern)) {
                ValuePattern value=(ValuePattern)pattern;
                if(value.Current.IsReadOnly) return result;
                string contents=value.Current.Value;
                if(contents.Length>2000) return new InputSnapshot { Error="当前输入超过 2000 字，请分段翻译" };
                result.Text=contents; result.Mode="value"; result.Editable=true;
            } else if(element.TryGetCurrentPattern(TextPattern.Pattern,out pattern)) {
                TextPattern text=(TextPattern)pattern;
                TextPatternRange[] selection=text.GetSelection();
                if(selection.Length!=1) return result;
                if(IsTerminal(element,focus)) return ReadTerminal(element,text,selection[0],focus);
                TextPatternRange paragraph=selection[0].Clone();
                object readOnly=paragraph.GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                if(!(readOnly is bool) || (bool)readOnly) return result;
                paragraph.ExpandToEnclosingUnit(TextUnit.Paragraph);
                string contents=paragraph.GetText(2001);
                if(contents.Length>2000) return new InputSnapshot { Error="当前段落超过 2000 字，请分段翻译" };
                result.Text=contents; result.Mode="paragraph"; result.Range=paragraph; result.Editable=true;
                TextPatternRange before=paragraph.Clone(); before.MoveEndpointByRange(TextPatternRangeEndpoint.End,selection[0],TextPatternRangeEndpoint.Start);
                result.Position=before.GetText(2001).Length+":"+selection[0].GetText(2001).Length;
            }
            if(!result.Editable) result.Error="此输入框暂不支持自动翻译，可使用翻译练习";
            System.Windows.Rect bounds=element.Current.BoundingRectangle;
            if(!bounds.IsEmpty) { result.X=(int)bounds.X; result.Y=(int)bounds.Y; result.Width=(int)bounds.Width; result.Height=(int)bounds.Height; }
            return result;
        }
        public static void Watch()
        {
            int stop=0;
            Task.Run(()=> { try { using(StreamReader input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8)) {
                string line; while((line=input.ReadLine())!=null) { if(line=="stop") break; try { Volatile.Write(ref watchActivity,new JavaScriptSerializer().Deserialize<InputWatchActivity>(line)); } catch { } }
            } } catch { } Interlocked.Exchange(ref stop,1); });
            using(StreamWriter output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)) { AutoFlush=true }) {
                while(Volatile.Read(ref stop)==0) {
                    RefreshWechat(Volatile.Read(ref watchActivity)); InputSnapshot snapshot=ReadFocused();
                    try { output.WriteLine(new JavaScriptSerializer().Serialize(snapshot)); } catch { return; }
                    Thread.Sleep(200);
                }
            }
        }
        public static string Replace(InputSnapshot expected,string english)
        {
            using(InputProbeGuard guard=new InputProbeGuard()) return ReplaceCore(expected,english,guard);
        }
        private static string ReplaceCore(InputSnapshot expected,string english,InputProbeGuard guard)
        {
            if(expected==null) return "原文已失效，请重新翻译";
            if(CompositionUiVisible()) return "输入法仍在选词，保留中文，待提交后再替换";
            if(ModifiersDown()) return "请松开组合键后再替换";
            if(!PointerMatches(expected)) return "鼠标位置已变化，请确认输入框后手动替换";
            if(expected.Mode=="wechat-prefix") return ReplaceWechat(expected,english);
            InputSnapshot current=ReadFocused();
            Application.DoEvents(); if(guard.Changed) return "检测到新的输入动作，未替换";
            if(!SameSource(expected,current)) return "输入内容或输入框已变化，请重新翻译后再替换";
            if(current.FocusHandle==0) return "无法确认输入框焦点，请使用复制";
            if(String.IsNullOrWhiteSpace(english) || english.Length>12000) return "译文无效，未修改输入框";
            if(current.Mode=="word") return ReplaceWord(expected,english,guard);
            if(current.Mode=="terminal") return ReplaceTerminal(current,english,guard);
            if(current.Mode=="whatsapp-value") return ReplaceWhatsAppValue(expected,english,guard);
            IntPtr window=new IntPtr(current.FocusHandle);
            IntPtr response;
            if(current.Mode=="value" && IsNativeEdit(window)) {
                // EM_SETSEL 的终点为 -1，使用指针重载设置整个已核对的输入框。
                if(SendSelection(window,0x00b1,IntPtr.Zero,new IntPtr(-1),2,500,out response)==IntPtr.Zero) return "输入框未响应，未替换";
                Application.DoEvents(); if(FocusWindow()!=window || guard.Changed || ReadNativeEdit(window).Text!=expected.Text) return "输入或焦点已变化，未替换";
                if(SendText(window,0x00c2,new IntPtr(1),english,2,500,out response)==IntPtr.Zero) return "输入框未响应，替换未完成";
                return "";
            }
            // 自定义编辑器只有提供精确文本范围时才允许替换。
            if(current.Range==null && current.Mode=="value") {
                object value; AutomationElement element=AutomationElement.FocusedElement;
                if(element.TryGetCurrentPattern(TextPattern.Pattern,out value)) current.Range=((TextPattern)value).DocumentRange;
                else if(element.TryGetCurrentPattern(ValuePattern.Pattern,out value)) {
                    ValuePattern editor=(ValuePattern)value;
                    Application.DoEvents();
                    if(guard.Changed || FocusWindow()!=window || editor.Current.IsReadOnly || editor.Current.Value!=expected.Text) return "输入框或内容已变化，未替换";
                    editor.SetValue(english.Replace("\r"," ").Replace("\n"," ").Replace("\t"," ")); return "";
                }
            }
            if(current.Range==null || current.Range.GetText(2001)!=current.Text) return "此输入框不支持安全替换，请使用复制";
            if(Native.Down(0x10) || Native.Down(0x11) || Native.Down(0x12) || Native.Down(0x5b) || Native.Down(0x5c)) return "请松开组合键后再点替换";
            if(current.Mode=="paragraph") {
                string trailing=Regex.Match(current.Text,@"[\r\n]+$").Value;
                if(trailing.Length>0) {
                    current.Range.MoveEndpointByUnit(TextPatternRangeEndpoint.End,TextUnit.Character,-trailing.Length);
                    if(current.Range.GetText(2001)!=current.Text.Substring(0,current.Text.Length-trailing.Length)) return "此输入框不支持准确替换，请使用复制";
                }
            }
            // 自定义编辑器只插入普通文字，避免换行字符触发发送动作。
            english=english.Replace("\r"," ").Replace("\n"," ").Replace("\t"," ");
            current.Range.Select();
            Application.DoEvents(); if(FocusWindow()!=window || guard.Changed || AutomationElement.FocusedElement==null) return "焦点或输入已变化，未替换";
            return InsertEnglish(english);
        }
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageTimeoutW")] private static extern IntPtr SendSelection(IntPtr window,uint message,IntPtr start,IntPtr end,uint flags,uint timeout,out IntPtr result);
        public static bool SameSource(InputSnapshot expected,InputSnapshot current)
        {
            return expected!=null && current!=null && expected.Editable && current.Editable && !current.Composing &&
                expected.Id==current.Id && expected.Text==current.Text && expected.Mode==current.Mode && expected.FocusHandle==current.FocusHandle && expected.Position==current.Position;
        }
        public static void ReplaceCommand()
        {
            string error;
            try {
                ReplacePayload payload;
                using(StreamReader input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8)) payload=new JavaScriptSerializer().Deserialize<ReplacePayload>(input.ReadToEnd());
                error=Replace(payload.Source,payload.English);
            } catch { error="此输入框暂时无法替换，请使用复制"; }
            using(StreamWriter writer=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false))) writer.Write(new JavaScriptSerializer().Serialize(error));
        }
    }
    public sealed class ReplacePayload { public InputSnapshot Source; public string English; }

    // 将第三方界面读取放在子进程内，避免某个应用卡住主界面。
    public sealed class InputMonitor : IDisposable
    {
        private Process process;
        private InputSnapshot latest;
        private long received;
        private volatile bool responded;
        private int disposed;
        internal bool HasResponse { get { return responded; } }
        internal bool Fresh { get { return responded && Volatile.Read(ref disposed)==0 && Stopwatch.GetTimestamp()-Interlocked.Read(ref received)<Stopwatch.Frequency; } }
        public InputSnapshot Latest { get { return Volatile.Read(ref latest); } }
        private long lastActivity=-1;
        public void UpdateActivity(InputActivity activity)
        {
            if(process==null || activity==null || activity.Serial==lastActivity) return;
            try { process.StandardInput.WriteLine(new JavaScriptSerializer().Serialize(new InputWatchActivity { Serial=activity.Serial,Focus=activity.LastFocusHandle,At=activity.LastInputAt })); process.StandardInput.Flush(); lastActivity=activity.Serial; } catch { }
        }
        public bool Healthy { get { var owned=process; try { return owned!=null && !owned.HasExited && Stopwatch.GetTimestamp()-Interlocked.Read(ref received)<Stopwatch.Frequency*3; } catch(InvalidOperationException) { return false; } } }
        public InputMonitor()
        {
            process=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--input-watch") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardInput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8 });
            Interlocked.Exchange(ref received,Stopwatch.GetTimestamp());
            Process owned=process;
            owned.StandardError.ReadToEndAsync();
            Task.Run(async ()=> {
                try { string line; while((line=await owned.StandardOutput.ReadLineAsync())!=null) {
                    if(Volatile.Read(ref disposed)!=0) break;
                    if(line.Length>65536) throw new InvalidDataException("输入框读取结果过大");
                    Volatile.Write(ref latest,new JavaScriptSerializer().Deserialize<InputSnapshot>(line)); responded=true; Interlocked.Exchange(ref received,Stopwatch.GetTimestamp());
                } } catch { }
            });
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref disposed,1); responded=false;
            Process owned=process; process=null; Volatile.Write(ref latest,null);
            if(owned!=null) { try { if(!owned.HasExited) { owned.StandardInput.WriteLine("stop"); owned.StandardInput.Flush(); if(!owned.WaitForExit(300)) owned.Kill(); } } catch { } finally { owned.Dispose(); } }
        }
        public static async Task<string> ReplaceAsync(InputSnapshot source,string english,CancellationToken cancellation=default(CancellationToken))
        {
            return await Task.Run(()=> {
                cancellation.ThrowIfCancellationRequested();
                using(Process child=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--replace-input") { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,StandardOutputEncoding=Encoding.UTF8 })) {
                  using(cancellation.Register(()=> { try { if(!child.HasExited) child.Kill(); } catch { } })) {
                    byte[] data=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new ReplacePayload { Source=source,English=english }));
                    child.StandardInput.BaseStream.Write(data,0,data.Length); child.StandardInput.Close();
                    Task<string> output=child.StandardOutput.ReadToEndAsync();
                    if(!child.WaitForExit(2200)) { try { child.Kill(); } catch { } return "输入框响应超时，请检查是否已替换"; }
                    cancellation.ThrowIfCancellationRequested();
                    if(child.ExitCode!=0) return "替换未完成，请使用复制";
                    try { return new JavaScriptSerializer().Deserialize<string>(output.GetAwaiter().GetResult()); } catch { return "未收到替换结果，请检查输入框"; }
                  }
                }
            });
        }
    }

    public enum TranslationDirection { ChineseToEnglish, EnglishToChinese }

    public sealed class OnlineTranslator
    {
        private readonly Dictionary<string,string> cache=new Dictionary<string,string>();
        private readonly Dictionary<string,string> results=new Dictionary<string,string>();
        private readonly EnglishProofreader proofreader=new EnglishProofreader();
        private readonly Func<string,CancellationToken,Task<string>> correct;
        private readonly Func<string,TranslationDirection,CancellationToken,Task<string>> raw;
        private volatile bool correctionEnabled=true;
        private int correctionVersion;
        private readonly bool customPipeline;
        public bool CorrectionEnabled {
            get { return correctionEnabled; }
            set { if(correctionEnabled==value) return; correctionEnabled=value; Interlocked.Increment(ref correctionVersion); ClearCache(); }
        }
        public OnlineTranslator(Func<string,CancellationToken,Task<string>> correction=null,Func<string,TranslationDirection,CancellationToken,Task<string>> translation=null) { correct=correction ?? proofreader.CorrectAsync; raw=translation ?? TranslateRawAsync; customPipeline=correction!=null || translation!=null; }
        private static readonly SemaphoreSlim serial=new SemaphoreSlim(1,1);
        private static long lastRequest;
        public void ClearCache() { lock(cache) cache.Clear(); lock(results) results.Clear(); proofreader.ClearCache(); }
        public static List<string> Chunks(string text)
        {
            List<string> chunks=new List<string>(); int start=0;
            while(start<text.Length) {
                int end=start, bytes=0, boundary=-1,space=-1;
                while(end<text.Length) {
                    int count=Char.IsHighSurrogate(text[end]) && end+1<text.Length && Char.IsLowSurrogate(text[end+1]) ? 2 : 1;
                    int next=Encoding.UTF8.GetByteCount(text.Substring(end,count)); if(bytes+next>480) break;
                    bytes+=next; end+=count;
                    if("。！？!?\n；;".IndexOf(text[end-1])>=0) boundary=end;
                    if(Char.IsWhiteSpace(text[end-1])) space=end;
                }
                if(end<text.Length && boundary>start) end=boundary;
                else if(end<text.Length && space>start) end=space;
                foreach(System.Text.RegularExpressions.Match marker in ProtectedTranslation.Marker.Matches(text,start)) if(marker.Index<end && marker.Index+marker.Length>end) { end=marker.Index; break; }
                chunks.Add(text.Substring(start,end-start)); start=end;
            }
            return chunks;
        }
        public static TranslationDirection DirectionFor(string text) { return InputTranslationGate.HasChinese(text) ? TranslationDirection.ChineseToEnglish : TranslationDirection.EnglishToChinese; }
        public static string LanguagePair(TranslationDirection direction) { return direction==TranslationDirection.EnglishToChinese ? "en|zh-CN" : "zh-CN|en"; }
        public Task<string> TranslateAsync(string text,CancellationToken cancellation) { return TranslateAsync(text,TranslationDirection.ChineseToEnglish,cancellation); }
        public Task<string> TranslateAsync(string text,TranslationDirection direction,CancellationToken cancellation) { return RequestReliability.WithDeadlineAsync(token=>TranslateCoreAsync(text,direction,token),40000,cancellation); }
        private async Task<string> TranslateCoreAsync(string text,TranslationDirection direction,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if(String.IsNullOrWhiteSpace(text) || text.Length>2000) throw new InvalidOperationException("请输入不超过 2000 字的中文或英文");
            bool useCorrection=correctionEnabled; int version=Volatile.Read(ref correctionVersion);
            TranslationOptions options=TranslationPreferences.Current;
            string key=options.Revision+":"+options.Provider+":"+useCorrection+":"+LanguagePair(direction)+":"+text,found;
            lock(results) if(results.TryGetValue(key,out found)) return found;
            if(!customPipeline && options.Provider=="deepseek") {
                if(!useCorrection && direction==TranslationDirection.ChineseToEnglish && !InputTranslationGate.HasChinese(text)) return text;
                string ai=await DeepSeekTranslation.TranslateAsync(text,direction,useCorrection,options,cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if(version!=Volatile.Read(ref correctionVersion) || options.Revision!=TranslationPreferences.Current.Revision) throw new OperationCanceledException("翻译设置已变化");
                lock(results) { if(results.Count>=64) results.Clear(); results[key]=ai; } return ai;
            }
            string corrected=useCorrection ? await correct(text,cancellation).ConfigureAwait(false) : text;
            cancellation.ThrowIfCancellationRequested();
            if(version!=Volatile.Read(ref correctionVersion) || options.Revision!=TranslationPreferences.Current.Revision) throw new OperationCanceledException("翻译设置已变化");
            string result;
            if(direction==TranslationDirection.ChineseToEnglish && !InputTranslationGate.HasChinese(text)) result=corrected;
            else {
                result=await raw(corrected,direction,cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                if(version!=Volatile.Read(ref correctionVersion) || options.Revision!=TranslationPreferences.Current.Revision) throw new OperationCanceledException("翻译设置已变化");
                if(useCorrection && direction==TranslationDirection.ChineseToEnglish) result=await correct(result,cancellation).ConfigureAwait(false);
            }
            cancellation.ThrowIfCancellationRequested();
            if(version!=Volatile.Read(ref correctionVersion) || options.Revision!=TranslationPreferences.Current.Revision) throw new OperationCanceledException("翻译设置已变化");
            lock(results) { if(results.Count>=64) results.Clear(); results[key]=result; }
            return result;
        }
        internal Task<string> TranslateFreeForTestAsync(string text,CancellationToken cancellation) { return TranslateRawAsync(text,TranslationDirection.ChineseToEnglish,cancellation); }
        private async Task<string> TranslateRawAsync(string text,TranslationDirection direction,CancellationToken cancellation)
        {
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            text=text ?? "";
            if(text.Length==0 || text.Length>2000) throw new InvalidOperationException("请输入不超过 2000 字的中文或英文");
            if(!System.Text.RegularExpressions.Regex.IsMatch(TranslationText.Technical.Replace(text,""),@"[\p{L}\p{N}]")) return text;
            string prefix=LanguagePair(direction)+":";
            await serial.WaitAsync(cancellation);
            try {
                string found; lock(cache) if(cache.TryGetValue(prefix+text,out found)) return found;
                ProtectedTranslation protectedInput=new ProtectedTranslation(text);
                StringBuilder translated=new StringBuilder(); bool previousTranslatable=false;
                foreach(TranslationSegment segment in TranslationSegment.Paragraphs(protectedInput.Text)) {
                    cancellation.ThrowIfCancellationRequested();
                    if(segment.Literal) { translated.Append(segment.Text); previousTranslatable=false; continue; }
                    string chunk=segment.Text;
                    string result;
                    lock(cache) cache.TryGetValue(prefix+chunk,out result);
                    if(result==null) {
                        long delay=350-(Stopwatch.GetTimestamp()-lastRequest)*1000/Stopwatch.Frequency;
                        if(delay>0) await Task.Delay((int)delay,cancellation);
                        lastRequest=Stopwatch.GetTimestamp();
                        HttpWebRequest request=(HttpWebRequest)WebRequest.Create("https://api.mymemory.translated.net/get?q="+Uri.EscapeDataString(chunk)+"&langpair="+Uri.EscapeDataString(LanguagePair(direction)));
                        request.Timeout=12000; request.ReadWriteTimeout=12000; request.AllowAutoRedirect=false; request.UserAgent="HoverLex/"+AppVersion.Current; request.Proxy=WebRequest.DefaultWebProxy;
                        using(CancellationTokenSource timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                        using(timeout.Token.Register(()=>request.Abort())) {
                            timeout.CancelAfter(12000);
                            string json;
                            try {
                                using(WebResponse response=await request.GetResponseAsync())
                                using(StreamReader reader=new StreamReader(response.GetResponseStream(),Encoding.UTF8)) {
                                    char[] buffer=new char[32768]; int read=await reader.ReadAsync(buffer,0,buffer.Length);
                                    StringBuilder body=new StringBuilder();
                                    while(read>0) { body.Append(buffer,0,read); if(body.Length>256*1024) throw new InvalidDataException("翻译服务返回内容过大"); read=await reader.ReadAsync(buffer,0,buffer.Length); }
                                    json=body.ToString();
                                }
                            } catch(WebException) { cancellation.ThrowIfCancellationRequested(); throw new InvalidOperationException("暂时无法连接翻译服务，请稍后重试"); }
                            cancellation.ThrowIfCancellationRequested(); result=ParseResponse(json);
                        }
                        lock(cache) { if(cache.Count>=64) cache.Clear(); cache[prefix+chunk]=result; }
                    }
                    if(previousTranslatable && direction==TranslationDirection.ChineseToEnglish && segment.Before.Length==0 && translated.Length>0 && !Char.IsWhiteSpace(translated[translated.Length-1])) translated.Append(' ');
                    translated.Append(segment.Before).Append(result).Append(segment.After); previousTranslatable=true;
                }
                cancellation.ThrowIfCancellationRequested();
                try { found=protectedInput.Restore(translated.ToString()); } catch { lock(cache) cache.Clear(); throw; }
                lock(cache) { if(cache.Count>=64) cache.Clear(); cache[prefix+text]=found; }
                return found;
            } finally { serial.Release(); }
        }
        public static string ParseResponse(string json)
        {
            Dictionary<string,object> body=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
            object status, quota, data;
            if(body==null || !body.TryGetValue("responseStatus",out status)) throw new InvalidDataException("翻译服务返回格式异常");
            if((body.TryGetValue("quotaFinished",out quota) && quota is bool && (bool)quota) || Convert.ToInt32(status)==429) throw new InvalidOperationException("翻译服务额度已用完，请稍后再试");
            if(Convert.ToInt32(status)!=200 || !body.TryGetValue("responseData",out data)) throw new InvalidOperationException("翻译服务暂时未能完成，请稍后重试");
            var contents=data as Dictionary<string,object>; object translated;
            if(contents==null || !contents.TryGetValue("translatedText",out translated)) throw new InvalidDataException("翻译服务未返回译文");
            string result=WebUtility.HtmlDecode(Convert.ToString(translated)).Trim();
            if(result.Length==0 || result.Length>12000) throw new InvalidDataException("翻译服务返回了无效译文");
            return result;
        }
    }
}
