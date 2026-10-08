using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace HoverLex
{
    public static class TranslationTests
    {
        private static InputSnapshot Snapshot(string text,string id="field") { return new InputSnapshot { Id=id,Text=text,Editable=true,Mode="value",FocusHandle=42 }; }
        public static void Core(Action<bool,string> check)
        {
            InputTranslationGate gate=new InputTranslationGate();
            check(!gate.Observe(Snapshot("已有中文"),0,0,true),"translation does not send pre-existing input on focus");
            check(!gate.Observe(Snapshot("已有中文，继续输入"),1,100,true),"translation waits after an edit");
            check(!gate.Observe(Snapshot("已有中文，继续输入"),1,100+InputTranslationGate.DebounceMilliseconds-1,true),"translation debounce is enforced");
            check(gate.Observe(Snapshot("已有中文，继续输入"),1,100+InputTranslationGate.DebounceMilliseconds,true),"Chinese input triggers after 450ms");
            check(!gate.Observe(Snapshot("已有中文，继续输入"),1,900,true),"unchanged input is not sent repeatedly");
            int generation=gate.Generation;
            gate.Observe(Snapshot("继续修改"),2,1000,true);
            check(gate.Generation!=generation,"editing invalidates an in-flight translation");
            generation=gate.Generation; gate.Observe(Snapshot("其他窗口","other"),2,1100,true);
            check(gate.Generation!=generation && !gate.Observe(Snapshot("其他窗口","other"),2,1900,true),"focus change cancels translation without sending existing text");
            gate.Observe(Snapshot("英语 hello"),3,2000,true);
            generation=gate.Generation; gate.Observe(Snapshot("英语 hello"),3,2800,false);
            check(gate.Generation!=generation && gate.Source==null,"switch off invalidates translation immediately");
            gate=new InputTranslationGate(); gate.Observe(Snapshot(""),0,0,true);
            gate.Observe(Snapshot("hello"),1,100,true);
            check(gate.Observe(Snapshot("hello"),1,900,true),"English-only typing requests correction after debounce");
            gate.Observe(Snapshot("hello 你好"),2,1000,true);
            check(gate.Observe(Snapshot("hello 你好"),2,1800,true),"mixed Chinese and English is supported");
            gate=new InputTranslationGate(); gate.Observe(Snapshot(""),0,0,true,42,false);
            gate.Observe(Snapshot("I has an apple."),1,100,true,42,false);
            check(!gate.Observe(Snapshot("I has an apple."),1,900,true,42,false),"correction off prevents English-only typing from creating a request");
            gate.Observe(Snapshot("你好 I has an apple."),2,1000,true,42,false);
            check(gate.Observe(Snapshot("你好 I has an apple."),2,1800,true,42,false),"correction off retains Chinese and mixed input translation");
            gate=new InputTranslationGate();
            check(!gate.Observe(Snapshot("I has an apple."),0,0,true) && !gate.Observe(Snapshot("I has an apple."),0,900,true),"existing English is not corrected merely by focusing a field");
            gate.Observe(Snapshot("She go to school."),1,1000,true,42);
            check(gate.Observe(Snapshot("She go to school."),1,1700,true,42) && !gate.Observe(Snapshot("She go to school."),1,1800,true,42),"English correction waits for typing and never repeats unchanged input");
            gate=new InputTranslationGate(); gate.Observe(Snapshot(""),0,0,true);
            InputSnapshot composition=Snapshot(""); composition.Composing=true;
            check(!gate.Observe(composition,1,100,true),"active IME composition is not translated");
            gate.Observe(Snapshot("你好"),1,200,true);
            check(gate.Observe(Snapshot("你好"),1,1000,true),"committed IME text is translated");
            InputTranslationGate firstComposition=new InputTranslationGate(); firstComposition.Observe(composition,1,0,true);
            firstComposition.Observe(Snapshot("你好"),1,200,true);
            check(firstComposition.Observe(Snapshot("你好"),1,1000,true),"IME commit after first focus is retained without an extra keypress");
            gate.Observe(Snapshot("移动光标后的旧段落"),1,1100,true);
            check(!gate.Observe(Snapshot("移动光标后的旧段落"),1,1900,true),"caret movement alone does not translate an old paragraph");
            gate=new InputTranslationGate(); gate.Observe(Snapshot("你好"),1,100,true,42);
            check(gate.Observe(Snapshot("你好"),1,900,true,42),"first fast edit after focus is not lost");
            check(!InputReader.SameSource(Snapshot("你好"),Snapshot("已修改")) && !InputReader.SameSource(Snapshot("你好"),Snapshot("你好","other")),"replacement rejects changed text and changed field");
            string longText=String.Concat(Enumerable.Repeat("中文 API 😀，这是用于验证分段的测试句。",40));
            List<string> chunks=OnlineTranslator.Chunks(longText);
            check(String.Join("",chunks)==longText && chunks.All(c=>Encoding.UTF8.GetByteCount(c)<=480 && !Char.IsHighSurrogate(c[c.Length-1])),"UTF-8 chunks preserve Chinese and surrogate pairs");
            check(OnlineTranslator.ParseResponse("{\"responseStatus\":200,\"quotaFinished\":false,\"responseData\":{\"translatedText\":\"I&#39;m ready &amp; waiting.\"}}") == "I'm ready & waiting.","translation response decodes HTML entities");
            bool quota=false; try { OnlineTranslator.ParseResponse("{\"responseStatus\":429,\"quotaFinished\":true}"); } catch(InvalidOperationException) { quota=true; }
            check(quota,"quota errors are shown instead of being used as translations");
            bool malformed=false; try { OnlineTranslator.ParseResponse("{\"responseStatus\":200,\"responseData\":{}}"); } catch { malformed=true; }
            check(malformed,"malformed translation responses are rejected");
            using(CancellationTokenSource cancelled=new CancellationTokenSource()) {
                cancelled.Cancel(); bool stopped=false; try { new OnlineTranslator().TranslateAsync("你好",cancelled.Token).GetAwaiter().GetResult(); } catch(OperationCanceledException) { stopped=true; }
                check(stopped,"cancelled requests do not contact the translation service");
            }
            check(!Settings.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"missing-translation-settings.json")).InputTranslation,"input translation defaults to off for old installations");
            string settingsDirectory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"TestArtifacts","input-settings-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(settingsDirectory);
            string settingsPath=Path.Combine(settingsDirectory,"settings.json");
            new Settings { Enabled=false,Delay=550,InputTranslation=true }.Save(settingsPath);
            Settings loaded=Settings.Load(settingsPath);
            check(loaded.InputTranslation && !loaded.Enabled && loaded.Delay==550,"translation toggle persists independently of existing capture settings");
            check(!loaded.AutoReplaceTranslation,"automatic replacement defaults to off for existing settings");
            loaded.AutoReplaceTranslation=true; loaded.Save(settingsPath);
            check(Settings.Load(settingsPath).AutoReplaceTranslation,"automatic replacement mode is saved independently");
            check(loaded.EnglishCorrection,"old settings retain the previously enabled correction behavior");
            loaded.EnglishCorrection=false; loaded.Save(settingsPath); loaded=Settings.Load(settingsPath);
            check(!loaded.EnglishCorrection && loaded.InputTranslation && loaded.AutoReplaceTranslation,"correction off persists independently of translation and automatic replacement");
            loaded.EnglishCorrection=true; loaded.Save(settingsPath);
            check(Settings.Load(settingsPath).EnglishCorrection,"correction can be enabled again and remembered");
            string legacyPath=Path.Combine(settingsDirectory,"legacy.json");
            File.WriteAllText(legacyPath,"{\"Ocr\":true,\"InputTranslation\":true,\"AutoReplaceTranslation\":false,\"Delay\":500}");
            Settings legacy=Settings.Load(legacyPath);
            check(legacy.EnglishCorrection && legacy.InputTranslation && !legacy.AutoReplaceTranslation && legacy.Delay==500,"settings without the new field preserve previous correction behavior and other choices");
            gate=new InputTranslationGate(); gate.Observe(Snapshot(""),0,0,true);
            gate.Observe(Snapshot(""),1,100,true,42); gate.Observe(Snapshot("中文提交晚于键盘事件"),1,300,true,42);
            check(gate.Observe(Snapshot("中文提交晚于键盘事件"),1,1000,true,42),"late IME text update triggers after activity serial was already observed");
            int oldGeneration=gate.Generation; gate.Observe(Snapshot("中文提交晚于键盘事件"),2,1050,true,42);
            check(gate.Generation!=oldGeneration,"new key invalidates a translation before the text provider updates");
            InputSnapshot oldCaret=Snapshot("中文"); oldCaret.Position="1:1"; InputSnapshot movedCaret=Snapshot("中文"); movedCaret.Position="0:0";
            check(!InputReader.SameSource(oldCaret,movedCaret),"unchanged text with a moved caret cannot be replaced");
            string prompt,draft;
            check(InputReader.ParseTerminalDraft("PS F:\\project> 你好","PS F:\\project> 你好   ",out prompt,out draft) && draft=="你好","PowerShell adapter excludes the prompt from the draft");
            check(InputReader.ParseTerminalDraft("› 请帮我写代码","› 请帮我写代码",out prompt,out draft) && draft=="请帮我写代码","Codex CLI prompt is recognized");
            check(!InputReader.ParseTerminalDraft("程序输出：你好","程序输出：你好",out prompt,out draft),"terminal output is not treated as an input draft");
            check(!InputReader.ParseTerminalDraft("PS F:\\project> 你","PS F:\\project> 你好",out prompt,out draft),"terminal replacement is disabled while the caret is inside the draft");
            gate=new InputTranslationGate(); InputSnapshot awaiting=Snapshot("","wechat"); awaiting.Mode="wechat-prefix";
            gate.Observe(awaiting,0,0,true); gate.Observe(awaiting,1,100,true,42);
            InputSnapshot captured=Snapshot("微信兼容输入","wechat"); captured.Mode="wechat-prefix";
            gate.Observe(captured,1,900,true,42);
            check(gate.Observe(captured,1,1600,true,42),"delayed WeChat compatibility capture retains the typing trigger");
            gate=new InputTranslationGate(); gate.Observe(awaiting,0,0,true); gate.Observe(awaiting,1,100,true,42); captured.Debounced=true;
            check(gate.Observe(captured,1,900,true,42),"already debounced compatibility draft does not wait a second time");
            gate=new InputTranslationGate(); gate.Observe(Snapshot(""),0,0,true); gate.Observe(Snapshot("原中文"),1,100,true,42); gate.Observe(Snapshot("原中文"),2,200,true,42);
            check(!gate.Observe(Snapshot("原中文"),2,1000,true,42),"a new key with unchanged text does not retranslate an old draft during composition");
            gate=new InputTranslationGate(); gate.Observe(Snapshot(""),0,0,true); gate.Observe(Snapshot("终端输入"),1,100,true,42);
            gate.Observe(new InputSnapshot(),1,400,true,42);
            check(gate.Observe(Snapshot("终端输入"),1,900,true,42),"transient terminal repaint preserves the pending edit trigger");
            gate=new InputTranslationGate(); gate.Observe(Snapshot(""),0,0,true); gate.Observe(Snapshot(""),1,100,true,42);
            gate.Observe(new InputSnapshot(),1,200,true,42); gate.Observe(Snapshot("延迟提交"),1,300,true,42);
            check(gate.Observe(Snapshot("延迟提交"),1,1000,true,42),"transient provider failure retains delayed input submission");
            gate=new InputTranslationGate(); gate.Observe(Snapshot(""),0,0,true); gate.Observe(Snapshot("本框输入"),1,100,true,42); gate.Observe(new InputSnapshot(),1,200,true,42);
            InputSnapshot changedField=Snapshot("另一框原文","other"); changedField.FocusHandle=43;
            gate.Observe(changedField,1,300,true,42);
            check(!gate.Observe(changedField,1,1200,true,42),"provider recovery never translates pre-existing text in a different field");
            gate=new InputTranslationGate(); gate.Observe(Snapshot("Old English.","late-terminal"),1,100,true,42);
            gate.Observe(Snapshot("Old English.中文", "late-terminal"),1,200,true,42);
            check(gate.Observe(Snapshot("Old English.中文", "late-terminal"),1,900,true,42),"typing before the first provider snapshot survives delayed Chinese text refresh");
            gate=new InputTranslationGate(); gate.Observe(Snapshot(""),0,0,true);
            gate.Observe(Snapshot("I has an apple."),1,100,true,42);
            check(gate.Observe(Snapshot("I has an apple."),1,600,true,42),"English correction starts before a provider interruption");
            gate.Observe(null,1,700,true,42);
            check(gate.Observe(Snapshot("I has an apple."),1,900,true,42),"an interrupted English check resumes when the same input returns");
            gate.Complete(gate.Generation); gate.Observe(null,1,1000,true,42);
            check(!gate.Observe(Snapshot("I has an apple."),1,1200,true,42),"a completed English check does not repeat after a provider repaint");
            gate=new InputTranslationGate(); gate.Observe(Snapshot(""),0,0,true);
            gate.Observe(Snapshot("I has an apple."),1,100,true,42);
            InputSnapshot transientComposition=Snapshot("I has an apple."); transientComposition.Composing=true;
            gate.Observe(transientComposition,1,300,true,42);
            check(gate.Observe(Snapshot("I has an apple."),1,900,true,42),"a transient IME flag retains an unsubmitted English check");
            gate.Observe(null,1,1000,true,42);
            check(!gate.Observe(Snapshot("Old text in another field.","other"),1,1200,true,43) && !gate.Observe(Snapshot("Old text in another field.","other"),1,1900,true,43),"an interrupted English check never resumes in a different field");
        }
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flag);
        [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr key,IntPtr value);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll",EntryPoint="GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle,int index);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr handle,StringBuilder name,int length);
        public static int Run(bool online=false)
        {
            List<string> lines=new List<string>(); int exit=1;
            Action<bool,string> check=(ok,name)=>lines.Add((ok ? "PASS " : "FAIL ")+name);
            Core(check);
            using(Form fixture=new Form { Text="HoverLex input translation tests",ClientSize=new Size(660,350),TopMost=true,StartPosition=FormStartPosition.CenterScreen })
            using(TextBox editor=new TextBox { Multiline=true,Text="这是测试文字。",Bounds=new Rectangle(20,20,600,100) })
            using(TextBox other=new TextBox { Text="另一个输入框",Bounds=new Rectangle(20,140,500,35) })
            using(TextBox password=new TextBox { UseSystemPasswordChar=true,Text="不上传",Bounds=new Rectangle(20,190,500,35) })
            using(TextBox readOnly=new TextBox { ReadOnly=true,Text="只读不翻译",Bounds=new Rectangle(20,240,500,35) })
            {
                fixture.Controls.AddRange(new Control[] { editor,other,password,readOnly });
                fixture.Shown+=async delegate {
                    try {
                        ShowWindow(fixture.Handle,5); Native.SetWindowPos(fixture.Handle,new IntPtr(-1),0,0,0,0,0x0053); fixture.Activate(); editor.Focus(); await Task.Delay(500);
                        ShowWindow(fixture.Handle,5); Native.SetWindowPos(fixture.Handle,new IntPtr(-1),0,0,0,0,0x0053); fixture.Activate(); editor.Focus(); await Task.Delay(100);
                        if(!await FocusFixture(fixture,editor)) { lines.Add("BLOCKED another application has keyboard focus; input fixture was not read or modified"); exit=3; return; }
                        StringBuilder surface=new StringBuilder(256); GetClassName(GetForegroundWindow(),surface,256);
                        if(surface.ToString()=="LockScreenBackstopFrame") { lines.Add("BLOCKED Windows lock screen covers input fixture"); exit=3; return; }
                        InputSnapshot source=await Task.Run(()=>InputReader.ReadFocused());
                        StringBuilder covering=new StringBuilder(256); GetClassName(GetAncestor(WindowFromPoint(editor.PointToScreen(new Point(40,30))),2),covering,256);
                        lines.Add("INFO foreground="+surface+" fixture="+fixture.Handle+" underClass="+covering+" snapshotEditable="+source.Editable+" composing="+source.Composing+" focus="+source.FocusHandle+" expectedFocus="+editor.Handle+" mode="+source.Mode+" textLength="+source.Text.Length+" error="+source.Error);
                        if(covering.ToString()=="LockScreenBackstopFrame") { lines.Add("BLOCKED Windows lock screen covers input fixture"); exit=3; return; }
                        check(source.Editable && source.Text==editor.Text,"focused edit text can be read without keyboard logging");
                        using(InputMonitor monitor=new InputMonitor()) {
                            for(int i=0;i<12 && monitor.Latest==null;i++) await Task.Delay(200);
                            if(InputReader.FocusWindow()!=editor.Handle) { lines.Add("BLOCKED keyboard focus changed during monitor test"); exit=3; return; }
                            check(monitor.Healthy && monitor.Latest!=null && monitor.Latest.Text==editor.Text,"isolated input monitor reads and reports focused text");
                            if(monitor.Latest!=null) lines.Add("INFO monitor mode="+monitor.Latest.Mode+" textLength="+monitor.Latest.Text.Length+" error="+monitor.Latest.Error);
                        }
                        using(TranslationPanel panel=new TranslationPanel()) {
                            fixture.Activate(); editor.Focus(); panel.Result(source,"This is test text."); IntPtr focus=GetForegroundWindow(); panel.ShowNear(source); Application.DoEvents();
                            check((GetWindowLong(panel.Handle,-20)&8)!=0 && GetForegroundWindow()==focus,"English floating panel stays on top without stealing focus");
                            lines.Add("INFO panel="+panel.Handle+" exStyle="+GetWindowLong(panel.Handle,-20)+" foregroundBefore="+focus+" foregroundAfter="+GetForegroundWindow());
                            using(Bitmap preview=new Bitmap(panel.Width,panel.Height)) { panel.DrawToBitmap(preview,new Rectangle(Point.Empty,panel.Size)); preview.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"translation-preview.png")); }
                            Button dismiss=FindButton(panel,"关闭");
                            SendMessage(dismiss.Handle,0x0201,new IntPtr(1),new IntPtr(0x000a000a));
                            check(GetForegroundWindow()==focus,"floating action mouse-down preserves original input focus");
                            SendMessage(dismiss.Handle,0x0202,IntPtr.Zero,new IntPtr(0x000a000a));
                            check(!IsWindowVisible(panel.Handle) && GetForegroundWindow()==focus,"close button responds without activating the floating window");
                            panel.ShowNear(source); Application.DoEvents();
                            check(IsWindowVisible(panel.Handle) && GetForegroundWindow()==focus,"hidden English panel returns without stealing focus");
                            panel.Hide();
                        }
                        string error=await InputMonitor.ReplaceAsync(source,"This is test text.");
                        check(error.Length==0 && editor.Text=="This is test text.","explicit replacement inserts English into the verified field: "+error);
                        bool undo=editor.CanUndo; editor.Undo();
                        check(undo && editor.Text==source.Text,"native replacement preserves Ctrl+Z undo");
                        source=await Task.Run(()=>InputReader.ReadFocused()); editor.Text="输入内容已变化";
                        error=await InputMonitor.ReplaceAsync(source,"Stale result.");
                        check(error.Length>0 && editor.Text=="输入内容已变化","stale translation cannot overwrite a newer edit");
                        source=await Task.Run(()=>InputReader.ReadFocused()); other.Focus(); await Task.Delay(100);
                        error=await InputMonitor.ReplaceAsync(source,"Wrong field.");
                        check(error.Length>0 && other.Text=="另一个输入框" && editor.Text=="输入内容已变化","replacement refuses a different focused input");
                        if(!await FocusFixture(fixture,password)) { lines.Add("BLOCKED keyboard focus changed before password test"); exit=3; return; }
                        InputSnapshot protectedInput=await Task.Run(()=>InputReader.ReadFocused());
                        check(!protectedInput.Editable && protectedInput.Text.Length==0,"password content is excluded from input snapshots");
                        if(!await FocusFixture(fixture,readOnly)) { lines.Add("BLOCKED keyboard focus changed before read-only test"); exit=3; return; }
                        InputSnapshot readonlyInput=await Task.Run(()=>InputReader.ReadFocused());
                        check(!readonlyInput.Editable && readonlyInput.Text.Length==0,"read-only content is excluded from translation");
                        using(InputActivity activity=new InputActivity()) check(activity.Serial==0,"input activity hook starts and can be removed without intercepting content");
                        if(!await FocusFixture(fixture,editor)) { lines.Add("BLOCKED focus changed before automatic replacement tests"); exit=3; return; }
                        await AutomaticScenarios(fixture,editor,other,check);
                        await TabScenarios(fixture,editor,check);
                        await CorrectionScenarios(fixture,editor,check);
                        if(online) {
                        OnlineTranslator service=new OnlineTranslator(); Stopwatch watch=Stopwatch.StartNew();
                        string translation=await service.TranslateAsync("你好，明天下午三点开会。",CancellationToken.None);
                        check(!String.IsNullOrWhiteSpace(translation) && !InputTranslationGate.HasChinese(translation),"live MyMemory Chinese-to-English translation");
                        lines.Add("INFO live test translation="+translation+" elapsedMs="+watch.ElapsedMilliseconds);
                        watch.Restart(); string cached=await service.TranslateAsync("你好，明天下午三点开会。",CancellationToken.None);
                        check(cached==translation && watch.ElapsedMilliseconds<100,"repeat input uses memory cache without another network request");
                        service.ClearCache();
                        }
                        exit=lines.Any(l=>l.StartsWith("FAIL")) ? 1 : 0;
                    } catch(Exception error) {
                        bool focusBlocked=error is InvalidOperationException && error.Message.IndexOf("focus",StringComparison.OrdinalIgnoreCase)>=0;
                        lines.Add((focusBlocked ? "BLOCKED " : "FAIL ")+error);
                        exit=lines.Any(line=>line.StartsWith("FAIL")) ? 1 : focusBlocked ? 3 : 1;
                    }
                    finally { File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"translation-tests.txt"),lines,Encoding.UTF8); fixture.Close(); }
                };
                Application.Run(fixture);
            }
            return exit;
        }
        public static int RunCorrectionToggle()
        {
            List<string> lines=new List<string>(); int exit=1;
            using(Form fixture=new Form { Text="HoverLex correction switch tests",ClientSize=new Size(720,300),TopMost=true,StartPosition=FormStartPosition.CenterScreen })
            using(TextBox editor=new TextBox { Multiline=true,Bounds=new Rectangle(20,20,640,200) }) {
                fixture.Controls.Add(editor);
                fixture.Shown+=async delegate {
                    try {
                        if(!await FocusFixture(fixture,editor)) { lines.Add("BLOCKED own correction fixture lacks keyboard focus"); exit=3; return; }
                        await CorrectionScenarios(fixture,editor,(ok,name)=>lines.Add((ok ? "PASS " : "FAIL ")+name));
                        exit=lines.Any(line=>line.StartsWith("FAIL")) ? 1 : 0;
                    } catch(Exception error) { bool focusBlocked=error is InvalidOperationException && error.Message.IndexOf("focus",StringComparison.OrdinalIgnoreCase)>=0; lines.Add((focusBlocked ? "BLOCKED " : "FAIL ")+error.Message); exit=focusBlocked ? 3 : 1; }
                    finally { File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"correction-toggle-ui-tests.txt"),lines,Encoding.UTF8); fixture.Close(); }
                };
                Application.Run(fixture);
            }
            return exit;
        }
        private static Button FindButton(Control control,string text)
        {
            foreach(Control child in control.Controls) { Button button=child as Button; if(button!=null && button.Text==text) return button; Button nested=FindButton(child,text); if(nested!=null) return nested; }
            return null;
        }
        [StructLayout(LayoutKind.Sequential)] private struct Key { public ushort Vk,Scan; public uint Flags,Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X,Y; public uint Data,Flags,Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] private struct KeyUnion { [FieldOffset(0)] public Key Key; [FieldOffset(0)] public Mouse Mouse; }
        [StructLayout(LayoutKind.Sequential)] private struct TestInput { public uint Type; public KeyUnion Value; }
        [DllImport("user32.dll")] private static extern uint SendInput(uint count,TestInput[] input,int size);
        private static void TypeFixture(Form fixture,TextBox editor,string text)
        {
            if(GetForegroundWindow()!=fixture.Handle || InputReader.FocusWindow()!=editor.Handle) throw new InvalidOperationException("test fixture lost keyboard focus; no input was sent; foreground="+GetForegroundWindow()+" expected="+fixture.Handle+" focus="+InputReader.FocusWindow()+" expectedEditor="+editor.Handle);
            TypeOwnedWindow(fixture.Handle,editor.Handle,text);
        }
        internal static void TypeOwnedWindow(IntPtr window,IntPtr focus,string text)
        {
            if(GetForegroundWindow()!=window || InputReader.FocusWindow()!=focus) throw new InvalidOperationException("own fixture lost focus; no input sent");
            TestInput[] keys=new TestInput[text.Length*2];
            for(int n=0;n<text.Length;n++) { keys[n*2]=new TestInput { Type=1,Value=new KeyUnion { Key=new Key { Scan=text[n],Flags=4 } } }; keys[n*2+1]=new TestInput { Type=1,Value=new KeyUnion { Key=new Key { Scan=text[n],Flags=6 } } }; }
            if(SendInput((uint)keys.Length,keys,Marshal.SizeOf(typeof(TestInput)))!=(uint)keys.Length) throw new InvalidOperationException("test input was rejected");
        }
        internal static void PasteOwnedWindow(IntPtr window,IntPtr focus)
        {
            if(GetForegroundWindow()!=window || InputReader.FocusWindow()!=focus) throw new InvalidOperationException("own fixture lost focus; no paste sent");
            TestInput[] keys=new TestInput[4]; int[] codes={0x11,0x56,0x56,0x11};
            for(int n=0;n<4;n++) keys[n]=new TestInput { Type=1,Value=new KeyUnion { Key=new Key { Vk=(ushort)codes[n],Flags=n>=2 ? 2u : 0u } } };
            if(SendInput(4,keys,Marshal.SizeOf(typeof(TestInput)))!=4) throw new InvalidOperationException("test paste was rejected");
        }
        internal static void KeyOwnedWindow(IntPtr window,IntPtr focus,int key,int repeats=1)
        {
            if(GetForegroundWindow()!=window || InputReader.FocusWindow()!=focus) throw new InvalidOperationException("own fixture lost focus; no key sent");
            TestInput[] keys=new TestInput[repeats+1];
            for(int n=0;n<keys.Length;n++) keys[n]=new TestInput { Type=1,Value=new KeyUnion { Key=new Key { Vk=(ushort)key,Flags=n==repeats ? 2u : 0u } } };
            if(SendInput((uint)keys.Length,keys,Marshal.SizeOf(typeof(TestInput)))!=(uint)keys.Length) throw new InvalidOperationException("test key was rejected");
        }
        private static async Task<bool> WaitUntil(Func<bool> condition,int timeout=4500)
        {
            Stopwatch time=Stopwatch.StartNew(); while(time.ElapsedMilliseconds<timeout) { if(condition()) return true; await Task.Delay(50); } return condition();
        }
        private static async Task AutomaticScenarios(Form fixture,TextBox editor,TextBox other,Action<bool,string> check)
        {
            editor.Text=""; editor.Focus(); int requests=0; string status="";
            using(TranslationController controller=new TranslationController(message=>status=message,async (text,cancellation)=> { requests++; await Task.Delay(150,cancellation); return "Hello, world."; })) {
                controller.AllowedWindow=fixture.Handle.ToInt64(); controller.AutoReplace=true; check(controller.SetEnabled(true),"automatic replacement controller starts"); await Task.Delay(600);
                TypeFixture(fixture,editor,"你好世界");
                bool translated=await WaitUntil(()=>editor.Text=="Hello, world.");
                check(translated,"typing Chinese automatically replaces the verified native input: "+status);
                await Task.Delay(400);
                check(IsWindowVisible(controller.Panel.Handle) && FindButton(controller.Panel,"朗读").Enabled,"automatic replacement retains the pronunciation card after provider refresh");
                check(!FindButton(controller.Panel,"替换").Enabled && !FindButton(controller.Panel,"重译").Enabled,"completed replacement cannot apply the old translation again");
                check(requests==1,"programmatic English replacement does not create a translation loop");
                bool undo=editor.CanUndo; editor.Undo();
                check(undo && editor.Text=="你好世界","automatic native replacement can be undone");
                controller.SetEnabled(false);
                check(!IsWindowVisible(controller.Panel.Handle),"disabling input translation dismisses the pronunciation receipt");
            }
            editor.Text=""; editor.Focus(); TaskCompletionSource<string> response=new TaskCompletionSource<string>(); bool started=false;
            using(TranslationController controller=new TranslationController(message=>status=message,(text,cancellation)=> { started=true; return response.Task; })) {
                controller.AutoReplace=true; controller.AllowedWindow=fixture.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(600); TypeFixture(fixture,editor,"原文");
                bool sent=await WaitUntil(()=>started); TypeFixture(fixture,editor,"继续"); response.SetResult("Stale English."); await Task.Delay(250);
                check(sent && editor.Text=="原文继续","late translation never overwrites typing that arrived before provider refresh");
                controller.SetEnabled(false);
            }
            editor.Text=""; editor.Focus(); response=new TaskCompletionSource<string>(); started=false;
            using(TranslationController controller=new TranslationController(message=>status=message,(text,cancellation)=> { started=true; return response.Task; })) {
                controller.AutoReplace=true; controller.AllowedWindow=fixture.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(600); TypeFixture(fixture,editor,"待翻译");
                bool sent=await WaitUntil(()=>started); other.Focus(); response.SetResult("Wrong field."); await Task.Delay(350);
                check(sent && editor.Text=="待翻译" && other.Text=="另一个输入框","automatic replacement cancels when input focus changes"); controller.SetEnabled(false);
            }
            if(!await FocusFixture(fixture,editor)) throw new InvalidOperationException("test fixture lost focus before mode test");
            editor.Text=""; editor.Focus(); started=false;
            using(TranslationController controller=new TranslationController(message=>status=message,(text,cancellation)=> { started=true; return Task.FromResult("Keep Chinese."); })) {
                controller.AllowedWindow=fixture.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(600); TypeFixture(fixture,editor,"浮窗确认");
                bool sent=await WaitUntil(()=>started); await Task.Delay(400);
                check(sent && editor.Text=="浮窗确认","floating mode leaves the original Chinese in the field: started="+sent+" input="+editor.Text+" shown="+controller.Panel.IsShown+" ready="+controller.Panel.CanReplace+" status="+status);
                Button apply=FindButton(controller.Panel,"替换");
                SendMessage(apply.Handle,0x0201,new IntPtr(1),new IntPtr(0x000a000a)); SendMessage(apply.Handle,0x0202,IntPtr.Zero,new IntPtr(0x000a000a));
                bool applied=await WaitUntil(()=>editor.Text=="Keep Chinese."); await Task.Delay(400);
                check(applied && IsWindowVisible(controller.Panel.Handle) && FindButton(controller.Panel,"朗读").Enabled && !apply.Enabled,"manual replacement retains a playable receipt without enabling repeat replacement: applied="+applied+" input="+editor.Text+" shown="+controller.Panel.IsShown+" status="+status);
                other.Focus(); await Task.Delay(250);
                check(!IsWindowVisible(controller.Panel.Handle),"moving to another editor dismisses the completed pronunciation receipt"); controller.SetEnabled(false);
            }
            editor.Text=""; editor.Focus(); started=false; response=new TaskCompletionSource<string>();
            using(TranslationController controller=new TranslationController(message=>status=message,(text,cancellation)=> { started=true; return response.Task; })) {
                controller.AutoReplace=true; controller.AllowedWindow=fixture.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(600); TypeFixture(fixture,editor,"关闭前原文");
                bool sent=await WaitUntil(()=>started); controller.SetEnabled(false); response.SetResult("Disabled result."); await Task.Delay(300);
                check(sent && editor.Text=="关闭前原文","switching translation off cancels automatic replacement even when the service returns late");
            }
            editor.Text=""; editor.Focus(); started=false; response=new TaskCompletionSource<string>();
            using(TranslationController controller=new TranslationController(message=>status=message,(text,cancellation)=> { started=true; return response.Task; })) {
                controller.AutoReplace=true; controller.AllowedWindow=fixture.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(600); TypeFixture(fixture,editor,"切换模式原文");
                bool sent=await WaitUntil(()=>started); controller.AutoReplace=false; response.SetResult("Previous mode result."); await Task.Delay(300);
                check(sent && editor.Text=="切换模式原文","switching to floating mode invalidates an in-flight automatic replacement"); controller.SetEnabled(false);
            }
        }
        private static async Task TabScenarios(Form fixture,TextBox editor,Action<bool,string> check)
        {
            if(!await FocusFixture(fixture,editor)) throw new InvalidOperationException("own Tab fixture lost focus");
            editor.AcceptsTab=true; editor.Text=""; string status="";
            using(TranslationController controller=new TranslationController(message=>status=message,(text,cancel)=>Task.FromResult("Tab replacement."))) {
                controller.AllowedWindow=fixture.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(500); TypeFixture(fixture,editor,"快捷替换");
                bool ready=await WaitUntil(()=>controller.Panel.CanReplace && controller.Panel.IsShown);
                KeyOwnedWindow(fixture.Handle,editor.Handle,9,3);
                check(ready && await WaitUntil(()=>editor.Text=="Tab replacement."),"Tab replaces the ready translation in the original input");
                check(InputReader.FocusWindow()==editor.Handle && !editor.Text.Contains("\t"),"replacement Tab does not insert a tab or move input focus");
                KeyOwnedWindow(fixture.Handle,editor.Handle,9); await Task.Delay(200);
                check(editor.Text=="Tab replacement.\t","Tab retains its normal behavior after replacement is complete");
                controller.SetEnabled(false); editor.Text="关闭状态"; editor.SelectionStart=editor.TextLength; KeyOwnedWindow(fixture.Handle,editor.Handle,9); await Task.Delay(100);
                check(editor.Text=="关闭状态\t","disabled input translation never intercepts Tab");
            }
            editor.Text=""; TaskCompletionSource<string> response=new TaskCompletionSource<string>(); bool sent=false;
            using(TranslationController controller=new TranslationController(message=>status=message,(text,cancel)=> { sent=true; return response.Task; })) {
                controller.AllowedWindow=fixture.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(500); TypeFixture(fixture,editor,"等待译文"); await WaitUntil(()=>sent);
                KeyOwnedWindow(fixture.Handle,editor.Handle,9); response.SetResult("Late translation."); await Task.Delay(300);
                check(editor.Text=="等待译文\t","Tab during a pending translation remains normal and cancels the old result"); controller.SetEnabled(false);
            }
            editor.Text="";
            using(TranslationController controller=new TranslationController(message=>status=message,(text,cancel)=>Task.FromResult("Stale translation."))) {
                controller.AllowedWindow=fixture.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(500); TypeFixture(fixture,editor,"旧原文"); await WaitUntil(()=>controller.Panel.CanReplace);
                TypeFixture(fixture,editor,"继续"); KeyOwnedWindow(fixture.Handle,editor.Handle,9); await Task.Delay(250);
                check(editor.Text=="旧原文继续\t","Tab cannot replace stale text even before the monitor refreshes"); controller.SetEnabled(false);
            }
            editor.AcceptsTab=false;
            editor.Text=""; int excludedRequests=0;
            using(TranslationController controller=new TranslationController(message=>status=message,(text,cancel)=> { excludedRequests++; return Task.FromResult("Unexpected translation."); })) {
                controller.AllowedWindow=fixture.Handle.ToInt64(); controller.ExcludedFocusHandle=editor.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(500); TypeFixture(fixture,editor,"主输入框交给手动翻译"); await Task.Delay(1100);
                check(excludedRequests==0 && !controller.Panel.IsShown,"the manual search field is excluded from global automatic translation"); controller.SetEnabled(false);
            }
        }
        private static async Task CorrectionScenarios(Form fixture,TextBox editor,Action<bool,string> check)
        {
            if(!await FocusFixture(fixture,editor)) throw new InvalidOperationException("own correction fixture lost focus");
            editor.Text=""; editor.AcceptsTab=true; string correctionStatus="",captured="";
            using(TranslationController controller=new TranslationController(message=>correctionStatus=message,(text,cancel)=> { captured=text; return Task.FromResult(text=="I has an apple." ? "I have an apple." : text); })) {
                controller.AllowedWindow=fixture.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(500);
                TypeFixture(fixture,editor,"I has an apple.");
                bool ready=await WaitUntil(()=>controller.Panel.CanReplace && controller.Panel.English=="I have an apple.");
                if(!ready && (GetForegroundWindow()!=fixture.Handle || InputReader.FocusWindow()!=editor.Handle)) throw new InvalidOperationException("own correction fixture lost keyboard focus during the request");
                check(ready && FindButton(controller.Panel,"替换").Enabled,"English-only typing displays a replaceable correction: input="+editor.Text+" captured="+captured+" status="+correctionStatus);
                using(Bitmap preview=new Bitmap(controller.Panel.Width,controller.Panel.Height)) { controller.Panel.DrawToBitmap(preview,new Rectangle(Point.Empty,controller.Panel.Size)); preview.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"correction-preview.png")); }
                KeyOwnedWindow(fixture.Handle,editor.Handle,9); bool applied=await WaitUntil(()=>editor.Text=="I have an apple.");
                check(applied && InputReader.FocusWindow()==editor.Handle,"Tab applies the corrected English and retains input focus");
                editor.Text=""; controller.EnglishCorrection=false; captured=""; await Task.Delay(350); TypeFixture(fixture,editor,"I has an apple."); await Task.Delay(1600);
                check(captured=="" && editor.Text=="I has an apple." && !controller.Panel.IsShown,"closing the correction switch suppresses pure English requests without disabling translation");
                editor.Text=""; controller.Dismiss(); await Task.Delay(350); TypeFixture(fixture,editor,"中文照常翻译");
                bool translated=await WaitUntil(()=>captured=="中文照常翻译" && controller.Panel.CanReplace);
                check(translated && controller.Enabled,"Chinese translation still works while English correction is off");
                controller.EnglishCorrection=true;
                check(!controller.Panel.IsShown,"changing the correction switch dismisses the previous translation");
                editor.Text=""; controller.Dismiss(); await Task.Delay(350); TypeFixture(fixture,editor,"I am ready."); await Task.Delay(1600);
                check(editor.Text=="I am ready." && !controller.Panel.IsShown,"correct English produces no replacement card and retains the input");
                controller.SetEnabled(false);
            }
            editor.Text=""; TaskCompletionSource<string> response=new TaskCompletionSource<string>(); bool started=false;
            using(TranslationController controller=new TranslationController(message=>correctionStatus=message,(text,cancel)=> { started=true; return response.Task; })) {
                controller.AllowedWindow=fixture.Handle.ToInt64(); controller.AutoReplace=true; controller.SetEnabled(true); await Task.Delay(500);
                if(!await FocusFixture(fixture,editor)) throw new InvalidOperationException("own pending-correction fixture lost focus");
                TypeFixture(fixture,editor,"I has an apple.");
                bool sent=await WaitUntil(()=>started);
                check(sent && !controller.Panel.IsShown,"pending English checks stay silent until an actual correction is ready");
                controller.EnglishCorrection=false; response.SetResult("I have an apple."); await Task.Delay(300);
                check(sent && editor.Text=="I has an apple." && !controller.Panel.IsShown,"switching correction off cancels an in-flight automatic replacement: started="+sent+" input="+editor.Text+" shown="+controller.Panel.IsShown+" status="+correctionStatus);
                controller.SetEnabled(false);
            }
            editor.Text=""; response=new TaskCompletionSource<string>(); started=false;
            using(TranslationController controller=new TranslationController(message=> { },(text,cancel)=> { started=true; return response.Task; })) {
                controller.AllowedWindow=fixture.Handle.ToInt64(); controller.AutoReplace=true; controller.SetEnabled(true); await Task.Delay(500); TypeFixture(fixture,editor,"I has an apple.");
                bool sent=await WaitUntil(()=>started); TypeFixture(fixture,editor," More text."); response.SetResult("I have an apple."); await Task.Delay(300);
                check(sent && editor.Text=="I has an apple. More text." && !controller.Panel.CanReplace,"typing more English rejects a stale correction before automatic replacement: started="+sent+" input="+editor.Text+" replaceable="+controller.Panel.CanReplace);
                controller.SetEnabled(false);
            }
            editor.Text=""; started=false;
            using(TranslationController controller=new TranslationController(message=> { },(text,cancel)=> { started=true; return Task.FromResult("我有一个苹果。"); })) {
                controller.AllowedWindow=fixture.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(500);
                TypeFixture(fixture,editor,"I has an apple."); bool sent=await WaitUntil(()=>started); await Task.Delay(300);
                check(sent && controller.Panel.IsShown && !controller.Panel.CanReplace && controller.Panel.English=="英文纠错结果无效 · 保留原文，请重试" && editor.Text=="I has an apple.","an invalid English correction shows a retryable error without applying Chinese text");
                controller.SetEnabled(false);
            }
            editor.Text=""; int attempts=0;
            using(TranslationController controller=new TranslationController(message=>correctionStatus=message,(text,cancel)=> {
                attempts++; if(attempts==1) throw new InvalidOperationException("Temporary checker failure");
                return Task.FromResult("I have an apple.");
            })) {
                controller.AllowedWindow=fixture.Handle.ToInt64(); controller.SetEnabled(true); await Task.Delay(500);
                TypeFixture(fixture,editor,"I has an apple.");
                bool failed=await WaitUntil(()=>controller.Panel.IsShown && controller.Panel.English=="Temporary checker failure");
                Button retry=FindButton(controller.Panel,"重试");
                check(failed && retry!=null && retry.Enabled && !controller.Panel.CanReplace && editor.Text=="I has an apple.","an English checker failure displays a visible retry card and preserves input");
                if(failed && retry!=null) controller.Panel.Retry();
                check(await WaitUntil(()=>controller.Panel.CanReplace && controller.Panel.English=="I have an apple.") && attempts==2 && editor.Text=="I has an apple.","retry checks the same English again without another keystroke or automatic replacement");
                controller.SetEnabled(false);
            }
        }
        private static async Task<bool> FocusFixture(Form form,Control editor)
        {
            for(int attempt=0;attempt<5;attempt++) {
                ShowWindow(form.Handle,9); ShowWindow(form.Handle,5); form.WindowState=FormWindowState.Normal;
                SetForegroundWindow(form.Handle); form.Activate(); editor.Focus(); await Task.Delay(80);
                if(GetForegroundWindow()==form.Handle && InputReader.FocusWindow()==editor.Handle) return true;
            }
            return false;
        }
    }
}
