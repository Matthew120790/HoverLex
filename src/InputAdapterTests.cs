using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace HoverLex
{
    public static class InputAdapterTests
    {
        private const string Chinese="你好，明天下午三点开会。", English="Hello, we have a meeting tomorrow at 3pm.";
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder value,int length);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder value,int length);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback,IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window,EnumWindow callback,IntPtr data);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from,uint to,bool attach);
        private delegate bool EnumWindow(IntPtr window,IntPtr data);
        private static string ClassName(IntPtr window) { StringBuilder b=new StringBuilder(256); GetClassName(window,b,256); return b.ToString(); }
        private static object Get(object value,string name,params object[] args) { return value.GetType().InvokeMember(name,BindingFlags.GetProperty,null,value,args); }
        private static object Call(object value,string name,params object[] args) { return value.GetType().InvokeMember(name,BindingFlags.InvokeMethod,null,value,args); }
        private static void Set(object value,string name,object data) { value.GetType().InvokeMember(name,BindingFlags.SetProperty,null,value,new object[] { data }); }
        private static void Release(object value) { if(value!=null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        private static void FocusTestWindow(IntPtr target)
        {
            uint pid; uint from=GetCurrentThreadId(),foreground=GetWindowThreadProcessId(GetForegroundWindow(),out pid);
            bool attached=foreground!=0 && foreground!=from && AttachThreadInput(from,foreground,true);
            try { SetForegroundWindow(target); } finally { if(attached) AttachThreadInput(from,foreground,false); }
        }
        public static int Run()
        {
            List<string> lines=new List<string>(); int exit=1;
            Action<bool,string> check=(ok,name)=>lines.Add((ok ? "PASS " : "FAIL ")+name);
            using(Form fixture=new Form { Text="HoverLex adapter tests",TopMost=true,ClientSize=new Size(680,260) })
            using(TextBox editor=new TextBox { Multiline=true,Bounds=new Rectangle(20,20,620,150),Text=Chinese+"保留后半句" }) {
                fixture.Controls.Add(editor); fixture.Shown+=async delegate {
                    try {
                        ShowWindow(fixture.Handle,5); fixture.Activate(); editor.Focus(); await Task.Delay(500);
                        ShowWindow(fixture.Handle,5); SetForegroundWindow(fixture.Handle); editor.Focus(); editor.SelectionStart=Chinese.Length;
                        await Task.Delay(100);
                        if(GetForegroundWindow()!=fixture.Handle || InputReader.FocusWindow()!=editor.Handle) { lines.Add("BLOCKED adapter fixture lacks keyboard focus"); exit=3; return; }
                        int start=editor.SelectionStart; string clipboardText=Clipboard.ContainsText() ? Clipboard.GetText() : null;
                        string[] formats=Clipboard.GetDataObject()==null ? new string[0] : Clipboard.GetDataObject().GetFormats(false);
                        InputSnapshot prefix=InputReader.ReadWechatPrefix(editor.Handle,true);
                        lines.Add("INFO own compatibility fixture selection="+editor.SelectionStart+":"+editor.SelectionLength);
                        check(prefix.Editable && prefix.Text==Chinese,"compatibility reader copies only the draft prefix before the caret: "+prefix.Error);
                        check(editor.Text==Chinese+"保留后半句" && editor.SelectionStart==start && editor.SelectionLength==0,"compatibility reader preserves draft suffix and original caret position");
                        string[] after=Clipboard.GetDataObject()==null ? new string[0] : Clipboard.GetDataObject().GetFormats(false);
                        check(formats.OrderBy(s=>s).SequenceEqual(after.OrderBy(s=>s)) && clipboardText==(Clipboard.ContainsText() ? Clipboard.GetText() : null),"compatibility reader restores clipboard text and available formats");
                        editor.SelectionStart=Chinese.Length; editor.SelectionLength=0; bool continued=false;
                        using(System.Windows.Forms.Timer continuedTyping=new System.Windows.Forms.Timer { Interval=50 }) {
                            continuedTyping.Tick+=delegate { continuedTyping.Stop(); TranslationTests.TypeOwnedWindow(fixture.Handle,editor.Handle,"继续"); continued=true; };
                            continuedTyping.Start(); InputReader.ReadWechatPrefix(editor.Handle,true); await Task.Delay(250);
                            check(continued && editor.Text==Chinese+"继续保留后半句","typing during compatibility selection is replayed at the original caret without erasing the draft");
                        }
                        fixture.TopMost=false;
                        await Word(check,lines);
                        await Terminal(check,lines);
                        await Terminal(check,lines,true);
                        exit=lines.Any(l=>l.StartsWith("FAIL")) ? 1 : lines.Any(l=>l.StartsWith("BLOCKED")) ? 3 : 0;
                    } catch(Exception error) { lines.Add("FAIL "+error); }
                    finally { File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"input-adapter-tests.txt"),lines,Encoding.UTF8); fixture.Close(); }
                };
                Application.Run(fixture);
            }
            return exit;
        }
        private static async Task Word(Action<bool,string> check,List<string> lines)
        {
            Type type=Type.GetTypeFromProgID("Word.Application"); if(type==null) { lines.Add("BLOCKED Word is not installed"); return; }
            object app=null,docs=null,doc=null,content=null,selection=null,window=null,range=null;
            try {
                app=Activator.CreateInstance(type); Set(app,"Visible",false); docs=Get(app,"Documents"); doc=Call(docs,"Add",Type.Missing,Type.Missing,Type.Missing,Type.Missing);
                content=Get(doc,"Content"); Set(content,"Text","历史段落：不可翻译。\r"+Chinese+"\r");
                selection=Get(app,"Selection");
                // 从实际内容计算测试段落起点，避免硬编码汉字长度。
                string ownText=Convert.ToString(Get(content,"Text")); int start=ownText.IndexOf(Chinese,StringComparison.Ordinal);
                Call(selection,"SetRange",start+Chinese.Length,start+Chinese.Length);
                Set(app,"Visible",true); Call(doc,"Activate"); window=Get(app,"ActiveWindow"); IntPtr hwnd=new IntPtr(Convert.ToInt64(Get(window,"Hwnd")));
                ShowWindow(hwnd,5); SetForegroundWindow(hwnd); await Task.Delay(500);
                if(GetForegroundWindow()!=hwnd) { lines.Add("BLOCKED own Word fixture could not receive focus"); return; }
                InputSnapshot source=InputReader.ReadFocused();
                lines.Add("INFO Word mode="+source.Mode+" length="+source.Text.Length+" focusClass="+ClassName(InputReader.FocusWindow())+" error="+source.Error);
                lines.Add("INFO own Word sample="+new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(source.Text)+" fixture="+new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(ownText)+" position="+source.Position);
                check(source.Mode=="word" && source.Text==Chinese,"real Word reader returns only the current paragraph without document history");
                string error=await InputMonitor.ReplaceAsync(source,English);
                check(error.Length==0 && Convert.ToString(Get(content,"Text"))==ownText.Replace(Chinese,English),"real Word replacement preserves other paragraphs and paragraph marks: "+error);
                Call(doc,"Undo",1);
                check(Convert.ToString(Get(content,"Text"))==ownText,"real Word replacement has a single undo record");
                Call(selection,"SetRange",start+Chinese.Length,start+Chinese.Length);
                source=InputReader.ReadFocused(); Call(selection,"SetRange",start,start);
                error=await InputMonitor.ReplaceAsync(source,"Wrong caret.");
                check(error.Length>0 && Convert.ToString(Get(content,"Text"))==ownText,"real Word replacement rejects a moved caret");
                Call(selection,"SetRange",start,start+Chinese.Length); int requests=0; string status="";
                using(TranslationController controller=new TranslationController(message=>status=message,(text,cancellation)=> { requests++; return Task.FromResult(English); })) {
                    controller.AllowedWindow=hwnd.ToInt64(); controller.AutoReplace=true; controller.SetEnabled(true); await Task.Delay(500);
                    object active=Get(app,"ActiveDocument"); bool owned=Convert.ToString(Get(active,"Name"))==Convert.ToString(Get(doc,"Name")); Release(active);
                    if(!owned || GetForegroundWindow()!=hwnd) { lines.Add("BLOCKED own Word document lost focus before typing test"); return; }
                    TranslationTests.TypeOwnedWindow(hwnd,InputReader.FocusWindow(),"中文自动替换测试。");
                    Stopwatch timer=Stopwatch.StartNew(); while(timer.ElapsedMilliseconds<6500 && !status.StartsWith("已自动替换成英文")) await Task.Delay(50);
                    var activity=(InputActivity)typeof(TranslationController).GetField("activity",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
                    var monitor=(InputMonitor)typeof(TranslationController).GetField("monitor",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(controller);
                    InputSnapshot latest=monitor.Latest;
                    lines.Add("INFO own Word activity="+activity.Serial+" typedFocus="+activity.LastFocusHandle+" nowFocus="+InputReader.FocusWindow()+" latestMode="+(latest==null ? "null" : latest.Mode)+" composing="+(latest!=null && latest.Composing)+" latestLength="+(latest==null ? 0 : latest.Text.Length));
                    string completed=status; controller.SetEnabled(false);
                    check(completed.StartsWith("已自动替换成英文") && requests==1 && Convert.ToString(Get(content,"Text"))==ownText.Replace(Chinese,English),"real Word typing triggers automatic replacement and preserves other paragraphs: "+completed);
                }
                Call(selection,"SetRange",start,start+English.Length);
                using(TranslationController controller=new TranslationController(message=>status=message,(text,cancel)=>Task.FromResult(English))) {
                    controller.AllowedWindow=hwnd.ToInt64(); controller.SetEnabled(true); await Task.Delay(500); TranslationTests.TypeOwnedWindow(hwnd,InputReader.FocusWindow(),"快捷替换测试");
                    Stopwatch timer=Stopwatch.StartNew(); while(timer.ElapsedMilliseconds<6500 && !controller.Panel.CanReplace) await Task.Delay(50);
                    bool ready=controller.Panel.CanReplace; TranslationTests.KeyOwnedWindow(hwnd,InputReader.FocusWindow(),9);
                    timer.Restart(); while(timer.ElapsedMilliseconds<3500 && Convert.ToString(Get(content,"Text"))!=ownText.Replace(Chinese,English)) await Task.Delay(50);
                    check(ready && Convert.ToString(Get(content,"Text"))==ownText.Replace(Chinese,English),"real Word Tab replaces the ready paragraph without inserting a tab or changing other paragraphs");
                }
            } finally {
                if(doc!=null) try { Call(doc,"Close",0,Type.Missing,Type.Missing); } catch { }
                // 只关闭本测试创建的 Word；若期间出现其他文档，保留应用。
                if(app!=null) try { if(docs!=null && Convert.ToInt32(Get(docs,"Count"))==0) Call(app,"Quit",0,Type.Missing,Type.Missing); } catch { }
                Release(range); Release(selection); Release(content); Release(window); Release(doc); Release(docs); Release(app);
            }
        }
        private static async Task Terminal(Action<bool,string> check,List<string> lines,bool windowsTerminal=false)
        {
            string qa=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"TestArtifacts","terminal-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(qa);
            string title="HoverLex CLI fixture "+Guid.NewGuid().ToString("N"),stop=Path.Combine(qa,"stop"),report=Path.Combine(qa,"result.json"),script=Path.Combine(qa,"fixture.ps1");
            string code="[Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)\r\n$Host.UI.RawUI.WindowTitle='"+title+"'\r\n[Console]::WriteLine('历史中文输出：禁止作为输入发送')\r\n$row=[Console]::CursorTop\r\n[Console]::Write('› ')\r\n$draft=New-Object Text.StringBuilder\r\n$enter=0\r\nwhile(-not [IO.File]::Exists('"+stop.Replace("'","''")+"')) {\r\n if([Console]::KeyAvailable) { $key=[Console]::ReadKey($true); if($key.Key -eq 'Backspace') { if($draft.Length -gt 0) { $draft.Length-- } } elseif($key.Key -eq 'Enter') { $enter++ } elseif(-not [char]::IsControl($key.KeyChar)) { [void]$draft.Append($key.KeyChar); [void]0 } ; [Console]::SetCursorPosition(0,$row); [Console]::Write(' ' * ([Console]::BufferWidth-1)); [Console]::SetCursorPosition(0,$row); [Console]::Write('› '+$draft.ToString()); }; Start-Sleep -Milliseconds 10\r\n}\r\n[IO.File]::WriteAllText('"+report.Replace("'","''")+"',(@{Input=$draft.ToString();Enter=$enter}|ConvertTo-Json -Compress))";
            File.WriteAllText(script,code,new UTF8Encoding(true));
            string executable=windowsTerminal ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WindowsApps","wt.exe") : "powershell.exe";
            if(windowsTerminal && !File.Exists(executable)) { lines.Add("BLOCKED Windows Terminal is not installed"); return; }
            string arguments=(windowsTerminal ? "-w new new-tab --title \""+title+"\" powershell.exe " : "")+"-NoProfile -ExecutionPolicy Bypass -File \""+script+"\"";
            string label=windowsTerminal ? "Windows Terminal" : "console";
            using(Process process=Process.Start(new ProcessStartInfo(executable,arguments) { UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden })) try {
                IntPtr hwnd=IntPtr.Zero;
                for(int i=0;i<60 && hwnd==IntPtr.Zero;i++) {
                    EnumWindows((window,data)=> { StringBuilder b=new StringBuilder(256); GetWindowText(window,b,256); if(b.ToString().IndexOf(title,StringComparison.Ordinal)>=0) hwnd=window; return true; },IntPtr.Zero);
                    if(hwnd==IntPtr.Zero) await Task.Delay(100);
                }
                if(hwnd==IntPtr.Zero) { lines.Add("BLOCKED terminal fixture window was not found"); return; }
                ShowWindow(hwnd,5); SetForegroundWindow(hwnd); await Task.Delay(500);
                AutomationElement root=AutomationElement.FromHandle(hwnd);
                AutomationElement term=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ClassNameProperty,"TermControl"));
                if(term!=null) term.SetFocus();
                for(int n=0;n<5 && GetForegroundWindow()!=hwnd;n++) { SetForegroundWindow(hwnd); ShowWindow(hwnd,5); await Task.Delay(100); }
                lines.Add("INFO own terminal hwnd="+hwnd+" rootClass="+ClassName(hwnd)+" foreground="+GetForegroundWindow()+" focus="+InputReader.FocusWindow()+" focusRoot="+GetAncestor(InputReader.FocusWindow(),2));
                if(GetForegroundWindow()!=hwnd || GetAncestor(InputReader.FocusWindow(),2)!=hwnd) { lines.Add("BLOCKED terminal fixture lacks keyboard focus"); return; }
                TranslationTests.TypeOwnedWindow(hwnd,InputReader.FocusWindow(),Chinese); await Task.Delay(250);
                InputSnapshot source=InputReader.ReadFocused();
                lines.Add("INFO Terminal mode="+source.Mode+" length="+source.Text.Length+" focusClass="+ClassName(InputReader.FocusWindow())+" error="+source.Error);
                AutomationElement focused=AutomationElement.FocusedElement; object pat;
                if(focused.TryGetCurrentPattern(TextPattern.Pattern,out pat)) { var selections=((TextPattern)pat).GetSelection(); if(selections.Length==1) { var line=selections[0].Clone(); line.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Line); var before=line.Clone(); before.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.End,selections[0],System.Windows.Automation.Text.TextPatternRangeEndpoint.Start); lines.Add("INFO own console before="+new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(before.GetText(300))+" line="+new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(line.GetText(300))); } }
                check(source.Mode=="terminal" && source.Text==Chinese,"real "+label+" reader isolates current draft from old output and prompt");
                string error=await InputMonitor.ReplaceAsync(source,English);
                InputSnapshot translated=null; for(int n=0;n<30;n++) { await Task.Delay(100); translated=InputReader.ReadFocused(); if(translated.Text==English) break; }
                check(error.Length==0 && translated.Text==English,"real "+label+" replacement edits the current line without selecting output: "+error);
                int requests=0; string status="";
                using(TranslationController controller=new TranslationController(message=>status=message,(text,cancellation)=> { requests++; return Task.FromResult(English); })) {
                    controller.AllowedWindow=hwnd.ToInt64(); controller.AutoReplace=true; controller.SetEnabled(true); await Task.Delay(500);
                    TranslationTests.TypeOwnedWindow(hwnd,InputReader.FocusWindow(),"继续输入中文");
                    Stopwatch timer=Stopwatch.StartNew(); while(timer.ElapsedMilliseconds<6500 && !status.StartsWith("已自动替换成英文")) await Task.Delay(50);
                    string completed=status; controller.SetEnabled(false);
                    focused=AutomationElement.FocusedElement;
                    if(focused.TryGetCurrentPattern(TextPattern.Pattern,out pat)) { var selections=((TextPattern)pat).GetSelection(); if(selections.Length==1) { var line=selections[0].Clone(); line.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Line); var before=line.Clone(); before.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.End,selections[0],System.Windows.Automation.Text.TextPatternRangeEndpoint.Start); lines.Add("INFO own console after typing before="+new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(before.GetText(300))+" line="+new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(line.GetText(300))+" requests="+requests); } }
                    for(int n=0;n<30;n++) { await Task.Delay(100); translated=InputReader.ReadFocused(); if(translated.Text==English) break; }
                    check(completed.StartsWith("已自动替换成英文") && requests==1 && translated.Text==English,"real "+label+" typing triggers automatic replacement: "+completed);
                }
                using(TranslationController controller=new TranslationController(message=>status=message,(text,cancel)=>Task.FromResult(English))) {
                    controller.AllowedWindow=hwnd.ToInt64(); controller.SetEnabled(true); await Task.Delay(500); TranslationTests.TypeOwnedWindow(hwnd,InputReader.FocusWindow(),"快捷替换测试");
                    Stopwatch timer=Stopwatch.StartNew(); while(timer.ElapsedMilliseconds<6500 && !controller.Panel.CanReplace) await Task.Delay(50);
                    bool ready=controller.Panel.CanReplace; TranslationTests.KeyOwnedWindow(hwnd,InputReader.FocusWindow(),9);
                    timer.Restart(); do { await Task.Delay(100); translated=InputReader.ReadFocused(); } while(timer.ElapsedMilliseconds<3500 && translated.Text!=English);
                    check(ready && translated.Text==English,"real "+label+" Tab replaces the ready draft without terminal completion or Enter");
                }
                File.WriteAllText(stop,"stop"); for(int i=0;i<40 && !File.Exists(report);i++) await Task.Delay(100);
                if(File.Exists(report)) {
                    var result=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(report));
                    lines.Add("INFO own console input="+new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(result));
                    check(Convert.ToString(result["Input"])==English && Convert.ToInt32(result["Enter"])==0,"real "+label+" receives English without an Enter or command execution");
                } else check(false,"terminal fixture returned its input result");
            } finally { File.WriteAllText(stop,"stop"); }
        }
        public static int Wechat()
        {
            List<string> lines=new List<string>(); int exit=3;
            try {
                IntPtr main=IntPtr.Zero;
                EnumWindows((window,data)=> { if(ClassName(window)!="Qt51514QWindowIcon") return true; uint pid; GetWindowThreadProcessId(window,out pid); using(Process p=Process.GetProcessById((int)pid)) if(p.ProcessName=="Weixin") { var root=AutomationElement.FromHandle(window); if(root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ClassNameProperty,"MMUIRenderSubWindowHW"))!=null) { main=window; return false; } } return true; },IntPtr.Zero);
                if(main!=IntPtr.Zero) { ShowWindow(main,9); FocusTestWindow(main); System.Threading.Thread.Sleep(500); Application.DoEvents(); }
                lines.Add("INFO WeChat foregroundClass="+ClassName(GetForegroundWindow())+" focusClass="+ClassName(InputReader.FocusWindow()));
                lines.Add("INFO "+InputReader.WechatMetadata());
                InputSnapshot source=InputReader.ReadWechatPrefix(InputReader.FocusWindow());
                lines.Add("INFO WeChat sample length="+source.Text.Length+" hasChinese="+InputTranslationGate.HasChinese(source.Text)+" defaultTranslation="+(source.Text==English)+" agreedSuffix="+source.Text.EndsWith(Chinese)+" agreedOccurrences="+(source.Text.Split(new string[] { Chinese },StringSplitOptions.None).Length-1));
                string previousEnglish=English;
                if(source.Editable && source.Text!=Chinese && source.Text!=English) previousEnglish=new OnlineTranslator().TranslateAsync(Chinese,System.Threading.CancellationToken.None).GetAwaiter().GetResult();
                if(!source.Editable || (source.Text!=Chinese && source.Text!=English && source.Text!=previousEnglish)) { lines.Add("BLOCKED WeChat fixture is not focused or differs from the agreed sample; no text was replaced: "+source.Error); }
                else {
                    lines.Add("PASS real WeChat reads the agreed draft without changing it");
                    string error=InputReader.Replace(source,English);
                    Stopwatch settling=Stopwatch.StartNew(); while(settling.ElapsedMilliseconds<200) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                    InputSnapshot after=InputReader.ReadWechatPrefix(InputReader.FocusWindow());
                    bool ok=error.Length==0 && after.Text==English;
                    lines.Add((ok ? "PASS " : "FAIL ")+"real WeChat replaces only the agreed draft without sending a message: "+error); exit=ok ? 0 : 1;
                    if(ok) {
                        string status=""; int requests=0;
                        using(TranslationController controller=new TranslationController(message=>status=message,async (text,cancellation)=> {
                            requests++; if(text!=Chinese) throw new InvalidOperationException("WeChat test captured something other than the agreed draft: length="+text.Length+" startsEnglish="+text.StartsWith(English)+" endsChinese="+text.EndsWith(Chinese));
                            return await new OnlineTranslator().TranslateAsync(text,cancellation);
                        })) {
                            controller.AllowedWindow=GetForegroundWindow().ToInt64(); controller.AutoReplace=true; controller.SetEnabled(true);
                            Stopwatch wait=Stopwatch.StartNew(); while(wait.ElapsedMilliseconds<500) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                            if(!InputReader.SelectWechatTestPrefix(after)) { lines.Add("BLOCKED WeChat draft changed before automatic test"); exit=3; return exit; }
                            DataObject originalClipboard=InputReader.CopyClipboard(); uint pasteSequence=0;
                            try {
                                Clipboard.SetText(Chinese,TextDataFormat.UnicodeText); pasteSequence=GetClipboardSequenceNumber();
                                TranslationTests.PasteOwnedWindow(GetForegroundWindow(),InputReader.FocusWindow());
                                Stopwatch pasteSettle=Stopwatch.StartNew(); while(pasteSettle.ElapsedMilliseconds<250) { Application.DoEvents(); System.Threading.Thread.Sleep(5); }
                            } finally {
                                if(pasteSequence!=0 && GetClipboardSequenceNumber()==pasteSequence) { if(originalClipboard.GetFormats(false).Length==0) Clipboard.Clear(); else Clipboard.SetDataObject(originalClipboard,true); }
                            }
                            wait.Restart(); while(wait.ElapsedMilliseconds<18000 && !status.StartsWith("已自动替换成英文")) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                            string completedStatus=status; controller.SetEnabled(false);
                            settling.Restart(); while(settling.ElapsedMilliseconds<200) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                            InputSnapshot automatic=InputReader.ReadWechatPrefix(InputReader.FocusWindow());
                            bool passed=completedStatus.StartsWith("已自动替换成英文") && requests==1 && automatic.Editable && !InputTranslationGate.HasChinese(automatic.Text) && automatic.Text.Length>0;
                            lines.Add((passed ? "PASS " : "FAIL ")+"real WeChat typing triggers online translation and automatic replacement without sending: "+completedStatus+" requests="+requests);
                            if(!passed) exit=1;
                        }
                    }
                }
            } catch(Exception e) { lines.Add("FAIL "+e.GetType().Name+": "+e.Message); exit=1; }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"wechat-adapter-tests.txt"),lines,Encoding.UTF8); return exit;
        }
    }
}
