using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace HoverLex
{
    public sealed class InputWatchActivity { public long Serial, Focus, At; }

    public static partial class InputReader
    {
        internal static readonly IntPtr OwnInput=new IntPtr(0x484c5452);
        internal static long RootWindow(IntPtr focus) { return GetAncestor(focus,2).ToInt64(); }
        private static InputWatchActivity watchActivity;
        private static InputSnapshot wechatCached;
        private static long wechatReadSerial;
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window,ref Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint key,uint kind);
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window,uint kind);
        private delegate bool EnumTopWindow(IntPtr window,IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumTopWindow callback,IntPtr state);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window,int attribute,out int value,int size);
        internal static bool CompositionUiVisible()
        {
            bool visible=false;
            EnumWindows((window,state)=> {
                if(!IsWindowVisible(window)) return true;
                // 输入法后台窗口可能保留可见标记；DWM 隐藏的窗口不代表正在选字。
                int cloaked;
                if(DwmGetWindowAttribute(window,14,out cloaked,sizeof(int))==0 && cloaked!=0) return true;
                string cls=ClassName(window);
                bool candidate=cls.IndexOf("Candidate",StringComparison.OrdinalIgnoreCase)>=0 || cls=="MSCTFIME UI" || (cls=="Windows.UI.Core.CoreWindow" && ProcessName(window)=="TextInputHost");
                NativeRect rect;
                if(candidate && GetWindowRect(window,out rect) && rect.Right-rect.Left>20 && rect.Bottom-rect.Top>20 && SystemInformation.VirtualScreen.IntersectsWith(Rectangle.FromLTRB(rect.Left,rect.Top,rect.Right,rect.Bottom))) { visible=true; return false; }
                return true;
            },IntPtr.Zero); return visible;
        }
        internal static void StampPointer(InputSnapshot source) { Point point; if(GetCursorPos(out point)) { source.RequirePointer=true; source.PointerX=point.X; source.PointerY=point.Y; } }
        private static bool PointerMatches(InputSnapshot source) { Point point; return !source.RequirePointer || (GetCursorPos(out point) && point.X==source.PointerX && point.Y==source.PointerY); }
        [DllImport("oleacc.dll")] private static extern int AccessibleObjectFromWindow(IntPtr window,uint id,ref Guid iid,[MarshalAs(UnmanagedType.IDispatch)] out object value);
        private static string ClassName(IntPtr window) { StringBuilder b=new StringBuilder(256); GetClassName(window,b,256); return b.ToString(); }
        private static string ProcessName(IntPtr window) { uint pid; GetWindowThreadProcessId(window,out pid); try { using(Process p=Process.GetProcessById((int)pid)) return p.ProcessName; } catch { return ""; } }
        internal static bool ModifiersDown() { return Native.Down(0x10) || Native.Down(0x11) || Native.Down(0x12) || Native.Down(0x5b) || Native.Down(0x5c); }
        private static bool Composing(IntPtr focus)
        {
            IntPtr context=ImmGetContext(focus); if(context==IntPtr.Zero) return false;
            try { return ImmGetCompositionStringW(context,8,IntPtr.Zero,0)>0; } finally { ImmReleaseContext(focus,context); }
        }
        private static object ComGet(object value,string name,params object[] args) { return value.GetType().InvokeMember(name,BindingFlags.GetProperty,null,value,args); }
        private static object ComCall(object value,string name,params object[] args) { return value.GetType().InvokeMember(name,BindingFlags.InvokeMethod,null,value,args); }
        private static void Release(object value) { if(value!=null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        private static object WordWindow(IntPtr focus)
        {
            if(ClassName(focus)!="_WwG") return null;
            Guid iid=new Guid("00020400-0000-0000-C000-000000000046"); object window;
            return AccessibleObjectFromWindow(focus,0xfffffff0,ref iid,out window)==0 ? window : null;
        }
        private static InputSnapshot ReadWord(IntPtr focus)
        {
            if(ClassName(focus)!="_WwG") return null;
            object window=null,selection=null,range=null,paragraphs=null,paragraph=null,draft=null,document=null;
            try {
                window=WordWindow(focus); if(window==null) return new InputSnapshot { Error="Word 文档接口暂时不可用" };
                document=ComGet(window,"Document");
                if(Convert.ToBoolean(ComGet(document,"ReadOnly")) || Convert.ToInt32(ComGet(document,"ProtectionType"))!=-1) return new InputSnapshot { Error="只读或受保护的 Word 文档不翻译" };
                selection=ComGet(window,"Selection"); range=ComGet(selection,"Range");
                paragraphs=ComGet(range,"Paragraphs"); paragraph=ComGet(paragraphs,"First"); draft=ComGet(paragraph,"Range");
                string text=Convert.ToString(ComGet(draft,"Text")).TrimEnd('\r','\n','\a');
                if(text.Length>2000) return new InputSnapshot { Error="Word 当前段落超过 2000 字，请分段输入" };
                int start=Convert.ToInt32(ComGet(draft,"Start")),end=start+text.Length;
                InputSnapshot result=new InputSnapshot { Id="word:"+focus.ToInt64()+":"+ComGet(document,"Name")+":"+ComGet(range,"StoryType"),Mode="word",Editable=true,Composing=Composing(focus),Text=text,FocusHandle=focus.ToInt64(),Position=start+":"+end+":"+ComGet(selection,"Start")+":"+ComGet(selection,"End") };
                NativeRect bounds; if(GetWindowRect(focus,out bounds)) { result.X=bounds.Left; result.Y=bounds.Top; result.Width=bounds.Right-bounds.Left; result.Height=0; }
                return result;
            } catch { return new InputSnapshot { Error="Word 正忙，请完成当前对话框后继续输入" }; }
            finally { Release(draft); Release(paragraph); Release(paragraphs); Release(range); Release(selection); Release(document); Release(window); }
        }
        private static string ReplaceWord(InputSnapshot expected,string english,InputProbeGuard guard)
        {
            object window=null,selection=null,range=null,app=null,undo=null;
            bool recording=false;
            try {
                if(!SameSource(expected,ReadWord(new IntPtr(expected.FocusHandle))) || FocusWindow().ToInt64()!=expected.FocusHandle) return "Word 原文或光标已变化，未替换";
                string[] position=expected.Position.Split(':');
                window=WordWindow(new IntPtr(expected.FocusHandle)); selection=ComGet(window,"Selection"); range=ComGet(selection,"Range");
                ComCall(range,"SetRange",Int32.Parse(position[0]),Int32.Parse(position[1]));
                if(Convert.ToString(ComGet(range,"Text"))!=expected.Text) return "Word 段落已变化，未替换";
                app=ComGet(window,"Application");
                try { undo=ComGet(app,"UndoRecord"); ComCall(undo,"StartCustomRecord","HoverLex 中文转英文"); recording=true; } catch { }
                Application.DoEvents();
                if(guard.Changed || !SameSource(expected,ReadWord(new IntPtr(expected.FocusHandle))) || FocusWindow().ToInt64()!=expected.FocusHandle || Composing(new IntPtr(expected.FocusHandle))) return "焦点、原文或输入法状态已变化，未替换";
                range.GetType().InvokeMember("Text",BindingFlags.SetProperty,null,range,new object[] { english.Replace("\r"," ").Replace("\n"," ").Replace("\t"," ") });
                ComCall(selection,"SetRange",ComGet(range,"End"),ComGet(range,"End"));
                return "";
            } catch { return "Word 未能完成替换，请检查原文或使用复制"; }
            finally { if(recording) try { ComCall(undo,"EndCustomRecord"); } catch { } Release(undo); Release(app); Release(range); Release(selection); Release(window); }
        }
        private static bool IsTerminal(AutomationElement element,IntPtr focus)
        {
            string process=ProcessName(GetAncestor(focus,2));
            return (process.Equals("WindowsTerminal",StringComparison.OrdinalIgnoreCase) && element.Current.ClassName=="TermControl") || ClassName(GetAncestor(focus,2))=="ConsoleWindowClass";
        }
        internal static bool ParseTerminalDraft(string before,string full,out string prompt,out string draft)
        {
            prompt=draft=""; before=before.TrimEnd('\r','\n'); full=full.TrimEnd('\r','\n',' ');
            if(before.TrimEnd(' ')!=full) return false;
            Match match=Regex.Match(before,@"^(?<prompt>\s*(?:PS [^\r\n>]+>|[A-Za-z]:\\[^\r\n>]*>|[›❯»>] |\$ )\s?)(?<draft>[^\r\n]*)$");
            if(!match.Success) return false;
            prompt=match.Groups["prompt"].Value; draft=match.Groups["draft"].Value;
            return draft.Length<=2000;
        }
        private static InputSnapshot ReadTerminal(AutomationElement element,TextPattern text,TextPatternRange cursor,IntPtr focus)
        {
            InputSnapshot result=new InputSnapshot { Error="CLI：请在提示符后的输入行输入；复杂多行暂不自动替换" };
            if(cursor.CompareEndpoints(TextPatternRangeEndpoint.Start,cursor,TextPatternRangeEndpoint.End)!=0) return result;
            TextPatternRange line=cursor.Clone(); line.ExpandToEnclosingUnit(TextUnit.Line);
            TextPatternRange before=null; string prompt="",draft=""; bool parsed=false;
            for(int lineIndex=0;lineIndex<16;lineIndex++) {
                before=line.Clone(); before.MoveEndpointByRange(TextPatternRangeEndpoint.End,cursor,TextPatternRangeEndpoint.Start);
                string prefix=before.GetText(2201);
                if(ParseTerminalDraft(prefix,line.GetText(2201),out prompt,out draft)) { parsed=true; break; }
                // 仅沿软换行找输入提示符；实际换行表示输出/历史，立即停止。
                if(prefix.IndexOf('\r')>=0 || prefix.IndexOf('\n')>=0 || prefix.Length>2000 || line.MoveEndpointByUnit(TextPatternRangeEndpoint.Start,TextUnit.Line,-1)!=-1) break;
            }
            if(!parsed) return result;
            System.Windows.Rect bounds=element.Current.BoundingRectangle;
            System.Windows.Rect[] rectangles=line.GetBoundingRectangles(); string row=rectangles.Length>0 ? rectangles[0].X+":"+rectangles[0].Y : "";
            return new InputSnapshot { Id="terminal:"+String.Join(".",Array.ConvertAll(element.GetRuntimeId(),n=>n.ToString()))+":"+prompt+":"+row,Mode="terminal",Editable=true,Composing=Composing(focus),Text=draft,FocusHandle=focus.ToInt64(),Position=prompt+":"+before.GetText(2201).Length+":"+row,X=(int)bounds.X,Y=(int)bounds.Y,Width=(int)bounds.Width,Height=0 };
        }
        private static string ReplaceTerminal(InputSnapshot source,string english,InputProbeGuard guard)
        {
            // 终端的 Select 选中的是屏幕输出；仅用退格改当前输入，不发送 Ctrl+C、Ctrl+A 或回车。
            foreach(char c in source.Text) if(Char.IsSurrogate(c) || CharUnicodeInfo.GetUnicodeCategory(c)==UnicodeCategory.NonSpacingMark) return "CLI 当前输入包含特殊字符，请使用复制";
            if(!SameSource(source,ReadFocused()) || ModifiersDown()) return "CLI 原文或光标已变化，未替换";
            Input[] keys=new Input[source.Text.Length*2];
            for(int i=0;i<source.Text.Length;i++) { keys[i*2]=VirtualKey(8,false); keys[i*2+1]=VirtualKey(8,true); }
            Input[] letters=UnicodeKeys(english); Input[] all=new Input[keys.Length+letters.Length]; keys.CopyTo(all,0); letters.CopyTo(all,keys.Length);
            Application.DoEvents(); if(guard.Changed || !SameSource(source,ReadFocused()) || ModifiersDown()) return "CLI 检测到新的输入，未替换";
            return SendInput((uint)all.Length,all,Marshal.SizeOf(typeof(Input)))==(uint)all.Length ? "" : "CLI 未接受全部文字，请检查输入行";
        }
        private static string CaretPosition(IntPtr focus,out Point location)
        {
            uint pid; GuiThreadInfo info=new GuiThreadInfo { Size=Marshal.SizeOf(typeof(GuiThreadInfo)) };
            location=Point.Empty;
            if(!GetGUIThreadInfo(GetWindowThreadProcessId(focus,out pid),ref info) || info.Focus!=focus || info.Caret==IntPtr.Zero) {
                // 微信 4.x 的编辑位置由 Qt 内部维护，Windows 看不到标准光标。
                if(WechatMainCanvas(focus)) { NativeRect bounds; if(GetWindowRect(focus,out bounds)) location=new Point(bounds.Left+20,bounds.Bottom-150); return "qt:"+focus.ToInt64(); }
                return "";
            }
            location=new Point(info.CaretBounds.Left,info.CaretBounds.Top); ClientToScreen(info.Caret,ref location);
            return info.Caret.ToInt64()+":"+location.X+":"+location.Y;
        }
        private static bool WechatEditor(IntPtr focus)
        {
            string process=ProcessName(focus);
            if(!process.Equals("Weixin",StringComparison.OrdinalIgnoreCase) && !process.Equals("WeChat",StringComparison.OrdinalIgnoreCase)) return false;
            IntPtr top=GetAncestor(focus,2); string cls=ClassName(top);
            if(cls!="Qt51514QWindowIcon" && cls!="WeChatMainWndForPC" && cls!="mmui::MainWindow") return false;
            if(!WechatMainCanvas(focus)) return false;
            Point point; if(CaretPosition(focus,out point).Length==0) return false;
            try { AutomationElement el=AutomationElement.FocusedElement; for(int n=0;el!=null && n<10;n++,el=TreeWalker.RawViewWalker.GetParent(el)) if(el.Current.IsPassword) return false; } catch { return false; }
            return true;
        }
        private static bool WechatMainCanvas(IntPtr focus)
        {
            IntPtr top=GetAncestor(focus,2); if(top!=GetForegroundWindow()) return false;
            if(GetWindow(top,4)!=IntPtr.Zero) return false;
            uint pid; GetWindowThreadProcessId(top,out pid); string process=ProcessName(top);
            if(!process.Equals("Weixin",StringComparison.OrdinalIgnoreCase) && !process.Equals("WeChat",StringComparison.OrdinalIgnoreCase)) return false;
            string cls=ClassName(top); if(cls=="WeChatMainWndForPC" || cls=="mmui::MainWindow") return true;
            NativeRect rect; if(cls!="Qt51514QWindowIcon" || !GetWindowRect(top,out rect) || rect.Right-rect.Left<600 || rect.Bottom-rect.Top<420) return false;
            AutomationElement root=AutomationElement.FromHandle(top);
            return root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ClassNameProperty,"MMUIRenderSubWindowHW"))!=null;
        }
        internal static string WechatMetadata()
        {
            IntPtr focus=FocusWindow(); uint pid; GuiThreadInfo info=new GuiThreadInfo { Size=Marshal.SizeOf(typeof(GuiThreadInfo)) }; GetGUIThreadInfo(GetWindowThreadProcessId(focus,out pid),ref info);
            IntPtr context=ImmGetContext(focus); bool ime=context!=IntPtr.Zero; if(ime) ImmReleaseContext(focus,context);
            return "focus="+focus+" rootClass="+ClassName(GetAncestor(focus,2))+" active="+info.Active+" caret="+info.Caret+" ime="+ime+" editor="+WechatEditor(focus)+" composingUi="+CompositionUiVisible();
        }
        private static InputSnapshot ReadWechatCached(IntPtr focus)
        {
            if(!ProcessName(focus).Equals("Weixin",StringComparison.OrdinalIgnoreCase) && !ProcessName(focus).Equals("WeChat",StringComparison.OrdinalIgnoreCase)) { wechatCached=null; return null; }
            Point point; string position=CaretPosition(focus,out point);
            if(wechatCached!=null && wechatCached.FocusHandle==focus.ToInt64() && wechatCached.Position==position && !Composing(focus)) return wechatCached;
            if(!WechatEditor(focus)) return new InputSnapshot { Error="微信：请先点击聊天输入框；当前区域没有可确认的编辑光标" };
            return new InputSnapshot { Id="wechat:"+focus.ToInt64(),Mode="wechat-prefix",Editable=true,Composing=Composing(focus),Position=position,Error="微信兼容读取：请在聊天输入框输入中文后稍作停顿",FocusHandle=focus.ToInt64() };
        }
        private static void RefreshWechat(InputWatchActivity activity)
        {
            if(activity==null || activity.Serial==wechatReadSerial || Stopwatch.GetTimestamp()-activity.At<Stopwatch.Frequency*InputTranslationGate.DebounceMilliseconds/1000 || CompositionUiVisible()) return;
            IntPtr focus=FocusWindow(); if(focus.ToInt64()!=activity.Focus || !WechatEditor(focus) || Composing(focus) || ModifiersDown()) return;
            wechatReadSerial=activity.Serial; wechatCached=ReadWechatPrefix(focus);
            if(wechatCached.Editable) wechatCached.Debounced=true;
        }
        internal static DataObject CopyClipboard()
        {
            IDataObject original=Clipboard.GetDataObject(); DataObject saved=new DataObject();
            if(original==null) return saved;
            foreach(string format in original.GetFormats(false)) {
                object value=original.GetData(format,false); if(value==null) continue;
                MemoryStream stream=value as MemoryStream; Bitmap bitmap=value as Bitmap;
                if(stream!=null) value=new MemoryStream(stream.ToArray()); else if(bitmap!=null) value=new Bitmap(bitmap);
                else if(!(value is string) && !(value is string[]) && !(value is byte[]) && !value.GetType().IsPrimitive) throw new InvalidOperationException("剪贴板含复杂内容，本次兼容读取已跳过");
                saved.SetData(format,false,value);
            }
            return saved;
        }
        // 只在微信内复制光标前的输入；右箭头收回选择后仍在原光标位置。终端绝不走此路径。
        internal static InputSnapshot ReadWechatPrefix(IntPtr focus,bool fixture=false)
        {
            InputSnapshot result=new InputSnapshot { Error="微信兼容读取未完成，保留原输入" };
            if(FocusWindow()!=focus || (!fixture && !WechatEditor(focus)) || Composing(focus) || CompositionUiVisible() || ModifiersDown()) return result;
            Point point; string position=CaretPosition(focus,out point); if(position.Length==0) return result;
            DataObject saved=null; uint copiedSequence=0; bool selected=false;
            using(InputProbeGuard guard=new InputProbeGuard()) try {
                saved=CopyClipboard(); uint sequence=GetClipboardSequenceNumber();
                if(FocusWindow()!=focus || guard.Changed) return result;
                guard.PauseForSelection(focus);
                SendKeysBatch(new int[] { 0x11,0x10,0x24 },true); selected=true;
                SendKeysBatch(new int[] { 0x11,0x43 },false);
                Stopwatch wait=Stopwatch.StartNew();
                while(GetClipboardSequenceNumber()==sequence && wait.ElapsedMilliseconds<350 && !guard.Changed && FocusWindow()==focus) { Application.DoEvents(); Thread.Sleep(10); }
                uint ownerPid,focusPid; GetWindowThreadProcessId(GetClipboardOwner(),out ownerPid); GetWindowThreadProcessId(focus,out focusPid);
                copiedSequence=GetClipboardSequenceNumber();
                if(copiedSequence==sequence || ownerPid!=focusPid || guard.Changed || FocusWindow()!=focus) { if(fixture) result.Error="copy: sequenceChanged="+(copiedSequence!=sequence)+" ownerMatch="+(ownerPid==focusPid)+" inputChanged="+guard.Changed+" focusMatch="+(FocusWindow()==focus); if(copiedSequence==sequence || ownerPid!=focusPid || guard.ClipboardAction) copiedSequence=0; return result; }
                string text=Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : "";
                SendKeysBatch(new int[] { 0x27 },false); selected=false;
                Stopwatch restoring=Stopwatch.StartNew();
                while(restoring.ElapsedMilliseconds<350 && !guard.Changed && FocusWindow()==focus) { Application.DoEvents(); Point restored; if(restoring.ElapsedMilliseconds>=100 && CaretPosition(focus,out restored)==position) break; Thread.Sleep(10); }
                Point after; string actualPosition=CaretPosition(focus,out after);
                // 收回选择时应用可能滚动输入区，像素位置会变；编辑光标必须仍属于同一控件。
                if(guard.Changed || FocusWindow()!=focus || actualPosition.Length==0 || actualPosition.Split(':')[0]!=position.Split(':')[0] || text.Length>2000) return result;
                result=new InputSnapshot { Id="wechat:"+focus.ToInt64(),Mode="wechat-prefix",Editable=true,Text=text,FocusHandle=focus.ToInt64(),Position=actualPosition,X=after.X,Y=after.Y,Width=0,Height=20 }; return result;
            } catch(InvalidOperationException error) { result.Error=error.Message; return result; }
            catch { return result; }
            finally {
                if(selected && FocusWindow()==focus) { SendKeysBatch(new int[] { 0x27 },false); Stopwatch collapse=Stopwatch.StartNew(); while(collapse.ElapsedMilliseconds<100) { Application.DoEvents(); Thread.Sleep(10); } }
                // 若用户或其他程序在此期间复制了新内容，不覆盖它。
                if(saved!=null && copiedSequence!=0 && GetClipboardSequenceNumber()==copiedSequence) try { if(saved.GetFormats(false).Length==0) Clipboard.Clear(); else Clipboard.SetDataObject(saved,true); } catch { result.Editable=false; result.Error="剪贴板恢复失败，请检查剪贴板"; }
            }
        }
        private static string ReplaceWechat(InputSnapshot expected,string english)
        {
            IntPtr focus=new IntPtr(expected.FocusHandle);
            if(String.IsNullOrWhiteSpace(english) || english.Length>12000) return "译文无效，未替换";
            using(InputProbeGuard guard=new InputProbeGuard()) {
                InputSnapshot current=ReadWechatPrefix(focus);
                if(!SameSource(expected,current) || guard.Changed || ModifiersDown() || !PointerMatches(expected)) return "微信原文、光标或输入动作已变化，未替换";
                guard.PauseForSelection(focus);
                SendKeysBatch(new int[] { 0x11,0x10,0x24 },true); Application.DoEvents();
                if(FocusWindow()!=focus || guard.Changed || !PointerMatches(expected)) { if(FocusWindow()==focus) { SendKeysBatch(new int[] { 0x27 },false); Stopwatch collapse=Stopwatch.StartNew(); while(collapse.ElapsedMilliseconds<100) { Application.DoEvents(); Thread.Sleep(10); } } return "微信焦点或输入已变化，未替换"; }
                return InsertEnglish(english);
            }
        }
        internal static bool SelectWechatTestPrefix(InputSnapshot expected)
        {
            if(!SameSource(expected,ReadWechatPrefix(new IntPtr(expected.FocusHandle)))) return false;
            SendKeysBatch(new int[] { 0x11,0x10,0x24 },true); Application.DoEvents(); return FocusWindow().ToInt64()==expected.FocusHandle;
        }
        private static Input VirtualKey(int key,bool up) { return new Input { Type=1,Value=new InputUnion { Key=new KeyInput { Key=(ushort)key,Scan=(ushort)MapVirtualKey((uint)key,0),Flags=(up ? 2u : 0u)|((key>=0x21 && key<=0x2e) ? 1u : 0u),Extra=OwnInput } } }; }
        private static void SendKeysBatch(int[] keys,bool extended)
        {
            Input[] values=new Input[keys.Length*2];
            for(int n=0;n<keys.Length;n++) { values[n]=VirtualKey(keys[n],false); values[values.Length-1-n]=VirtualKey(keys[n],true); if(extended && n==keys.Length-1) { values[n].Value.Key.Flags|=1; values[values.Length-1-n].Value.Key.Flags|=1; } }
            if(SendInput((uint)values.Length,values,Marshal.SizeOf(typeof(Input)))!=(uint)values.Length) throw new InvalidOperationException("系统未接受输入动作");
        }
        private static Input[] UnicodeKeys(string english)
        {
            english=english.Replace("\r"," ").Replace("\n"," ").Replace("\t"," "); Input[] keys=new Input[english.Length*2];
            for(int i=0;i<english.Length;i++) { keys[i*2]=new Input { Type=1,Value=new InputUnion { Key=new KeyInput { Scan=english[i],Flags=4,Extra=OwnInput } } }; keys[i*2+1]=new Input { Type=1,Value=new InputUnion { Key=new KeyInput { Scan=english[i],Flags=6,Extra=OwnInput } } }; }
            return keys;
        }
        private static string InsertEnglish(string english) { Input[] keys=UnicodeKeys(english); return SendInput((uint)keys.Length,keys,Marshal.SizeOf(typeof(Input)))==(uint)keys.Length ? "" : "系统未接受全部文字，请检查输入框"; }
        internal static void ReplayProbeInput(System.Collections.Generic.List<int[]> events,IntPtr focus)
        {
            if(events.Count==0 || FocusWindow()!=focus) return;
            Rectangle desktop=SystemInformation.VirtualScreen; System.Collections.Generic.List<Input> values=new System.Collections.Generic.List<Input>(); IntPtr replay=new IntPtr(0x484c5250);
            foreach(int[] e in events) {
                if(e[0]==1) { uint flags=((e[3]&0x80)!=0 ? 2u : 0u)|((e[3]&1)!=0 ? 1u : 0u); ushort vk=(ushort)e[1]; if(vk==231) { vk=0; flags|=4; } values.Add(new Input { Type=1,Value=new InputUnion { Key=new KeyInput { Key=vk,Scan=(ushort)e[2],Flags=flags,Extra=replay } } }); }
                else {
                    uint flags=e[1]==0x201 ? 2u : e[1]==0x202 ? 4u : e[1]==0x204 ? 8u : e[1]==0x205 ? 16u : e[1]==0x207 ? 32u : e[1]==0x208 ? 64u : e[1]==0x20a ? 0x800u : e[1]==0x20e ? 0x1000u : 0u;
                    if(flags==0) continue;
                    values.Add(new Input { Type=0,Value=new InputUnion { Mouse=new MouseInput { X=(e[2]-desktop.Left)*65535/Math.Max(1,desktop.Width-1),Y=(e[3]-desktop.Top)*65535/Math.Max(1,desktop.Height-1),Data=(uint)(short)(e[4]>>16),Flags=flags|0xc001,Extra=replay } } });
                }
            }
            if(values.Count>0) SendInput((uint)values.Count,values.ToArray(),Marshal.SizeOf(typeof(Input)));
        }
    }
    internal sealed class InputProbeGuard : IDisposable
    {
        private delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int kind,Hook hook,IntPtr module,uint thread);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
        private readonly Hook keyboard,mouse;
        private IntPtr keyHook,mouseHook;
        private bool pause;
        private IntPtr target;
        private readonly System.Collections.Generic.List<int[]> buffered=new System.Collections.Generic.List<int[]>();
        public bool Changed { get; private set; }
        public bool ClipboardAction { get; private set; }
        internal InputProbeGuard()
        {
            keyboard=(code,message,data)=> { if(code>=0 && Marshal.ReadIntPtr(data,16)!=InputReader.OwnInput) { Changed=true; int key=Marshal.ReadInt32(data); if(pause && InputReader.FocusWindow()==target) { buffered.Add(new int[] {1,key,Marshal.ReadInt32(data,4),Marshal.ReadInt32(data,8)}); return new IntPtr(1); } if((key==0x43 || key==0x58) && Native.Down(0x11)) ClipboardAction=true; } return CallNextHookEx(keyHook,code,message,data); };
            mouse=(code,message,data)=> { if(code>=0 && message.ToInt32()!=0x200) { Changed=true; if(pause && InputReader.FocusWindow()==target) { buffered.Add(new int[] {0,message.ToInt32(),Marshal.ReadInt32(data),Marshal.ReadInt32(data,4),Marshal.ReadInt32(data,8)}); return new IntPtr(1); } if(message.ToInt32()==0x204 || message.ToInt32()==0x205) ClipboardAction=true; } return CallNextHookEx(mouseHook,code,message,data); };
            keyHook=SetWindowsHookEx(13,keyboard,GetModuleHandle(null),0); mouseHook=SetWindowsHookEx(14,mouse,GetModuleHandle(null),0);
            if(keyHook==IntPtr.Zero || mouseHook==IntPtr.Zero) { Dispose(); throw new InvalidOperationException("无法核对输入动作"); }
        }
        internal void PauseForSelection(IntPtr focus) { target=focus; pause=true; }
        public void Dispose() { if(keyHook!=IntPtr.Zero) UnhookWindowsHookEx(keyHook); if(mouseHook!=IntPtr.Zero) UnhookWindowsHookEx(mouseHook); keyHook=mouseHook=IntPtr.Zero; pause=false; InputReader.ReplayProbeInput(buffered,target); buffered.Clear(); }
    }
}
