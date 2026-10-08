using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Automation;

namespace HoverLex
{
    internal static class CaptureCompatibilityTests
    {
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int mode);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first,uint second,bool attach);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
        private delegate bool TopWindow(IntPtr window,IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(TopWindow callback,IntPtr state);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
        public static int Run(string mode)
        {
            List<string> lines=new List<string>(); Action<bool,string> check=(ok,name)=>lines.Add((ok ? "PASS " : "FAIL ")+name);
            try {
                string text="Curiosity makes learning easier.";
                RectangleF glyph=new RectangleF(10,10,12,24);
                check(ScreenReader.WordFromOffset(text,3,glyph,15,15,"fixture").Word=="Curiosity","character offset selects the whole source word");
                check(ScreenReader.WordFromOffset(text,9,glyph,15,15,"fixture")==null,"spaces do not return an adjacent word");
                check(ScreenReader.WordFromOffset(text,3,glyph,30,15,"fixture")==null,"closest insertion point outside the actual glyph is refused");
                check(ScreenReader.WordFromOffset(text,-1,glyph,15,15,"fixture")==null && ScreenReader.WordFromOffset(text,text.Length,glyph,15,15,"fixture")==null,"out-of-range offsets are refused");
                check(ScreenReader.WordFromOffset("你好 don't lose context",4,glyph,15,15,"fixture").Word=="don't","mixed-language context keeps complete apostrophe words");
                check(ScreenReader.WordFromOffset("well-known",6,glyph,15,15,"fixture").Word=="well-known","hyphenated words remain intact");
                check(ScreenReader.WordFromOffset("hello",2,new RectangleF(10,10,0,24),10,15,"fixture")==null,"empty character geometry is refused");
                Selection(check);
                Views(check);
                using(var lifecycle=new MainForm(true)) {
                    SelfTest.Prepare(lifecycle);
                    var hotkeys=(List<int>)typeof(MainForm).GetField("registered",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(lifecycle);
                    hotkeys.Add(999); lifecycle.Dispose(); lifecycle.Dispose();
                    check(lifecycle.IsDisposed && !lifecycle.IsHandleCreated,"closing and disposing again does not recreate the main window or raise a startup error");
                }
                if(mode=="--office") Office(check,lines);
                if(mode=="--terminal") { Terminal(true,check,lines); Terminal(false,check,lines); }
            } catch(Exception error) { check(false,error.ToString()); }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"capture-compatibility-tests.txt"),lines,Encoding.UTF8);
            return lines.Any(line=>line.StartsWith("FAIL")) ? 1 : 0;
        }
        private static void Selection(Action<bool,string> check)
        {
            using(var controller=new SelectionTranslationController(message=>{})) {
                const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
                Type type=typeof(SelectionTranslationController);
                controller.Enabled=true;
                var shortcut=(SelectionShortcut)type.GetField("shortcut",flags).GetValue(controller); shortcut.Key(0xa2,true);
                check(!controller.SuppressHover,"pending selection read does not prevent Ctrl hover lookup");
                type.GetField("selection",flags).SetValue(controller,new InputSnapshot { Error="无法读取选中文字" });
                check(!controller.SuppressHover,"unsupported selection reader cannot permanently block mouse lookup");
                type.GetField("selection",flags).SetValue(controller,new InputSnapshot { Text="Curiosity makes learning easier." });
                check(!controller.SuppressHover,"selected text cannot block lookup while Ctrl is held");
                shortcut.Key(0xa2,false);
                check(controller.SuppressHover,"a released Ctrl tap still gives selected text translation priority");
                controller.Dismiss(); check(!controller.SuppressHover,"dismissing selection translation releases hover lookup");
            }
        }
        private static void Views(Action<bool,string> check)
        {
            using(var panel=new LookupPanel()) {
                SelfTest.Prepare(panel); panel.SetFailure("微信当前区域未提供可定位的文字。可先选中英文，再轻按 Ctrl 翻译。");
                TableLayoutPanel layout=panel.Controls.OfType<TableLayoutPanel>().Single();
                check(layout.Controls.OfType<Label>().Any(label=>label.Text=="未读到英文") && !layout.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(button=>button.Text=="收藏词义").Enabled,"failed hover shows a text-specific explanation and cannot save an error");
                using(Bitmap image=new Bitmap(panel.Width,panel.Height)) { panel.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"capture-failure-preview.png")); }
            }
        }
        private static object Get(object value,string name,params object[] args) { return value.GetType().InvokeMember(name,BindingFlags.GetProperty,null,value,args); }
        private static object Call(object value,string name,params object[] args) { return value.GetType().InvokeMember(name,BindingFlags.InvokeMethod,null,value,args); }
        private static void Release(object value) { if(value!=null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
        private static void ShowOwnedFixture(IntPtr window)
        {
            ShowWindow(window,9);
            uint pid,thread=GetCurrentThreadId(),other=GetWindowThreadProcessId(GetForegroundWindow(),out pid);
            bool attached=other!=0 && other!=thread && AttachThreadInput(thread,other,true);
            try { SetForegroundWindow(window); } finally { if(attached) AttachThreadInput(thread,other,false); }
            Application.DoEvents(); System.Threading.Thread.Sleep(300);
        }
        private static void Office(Action<bool,string> check,List<string> lines)
        {
            object app=null,docs=null,document=null,range=null,window=null;
            const string sample="Curiosity makes learning easier.\r";
            string baseline=null;
            try {
                try { app=Marshal.GetActiveObject("kwps.Application"); } catch { lines.Add("SKIP WPS not running or no automation server"); return; }
                docs=Get(app,"Documents"); document=Call(docs,"Add"); range=Get(document,"Content");
                range.GetType().InvokeMember("Text",BindingFlags.SetProperty,null,range,new object[] {sample});
                object content=Get(document,"Content"); try { baseline=Convert.ToString(Get(content,"Text")); } finally { Release(content); }
                window=Get(document,"ActiveWindow"); IntPtr handle=new IntPtr(Convert.ToInt64(Get(window,"Hwnd")));
                ShowOwnedFixture(GetAncestor(handle,2));
                Call(range,"SetRange",0,9);
                object[] args={0,0,0,0,range}; ParameterModifier refs=new ParameterModifier(5); for(int i=0;i<4;i++) refs[i]=true;
                window.GetType().InvokeMember("GetPoint",BindingFlags.InvokeMethod,null,window,args,new[] {refs},CultureInfo.InvariantCulture,null);
                int x=Convert.ToInt32(args[0])+Convert.ToInt32(args[2])/2,y=Convert.ToInt32(args[1])+Convert.ToInt32(args[3])/2;
                lines.Add("INFO WPS owned fixture point="+x+","+y);
                ScreenReader.CaptureTrace=message=>lines.Add("INFO "+message);
                CaptureResult result=ScreenReader.ReadOfficePoint(handle,x,y,"wps");
                lines.Add("INFO WPS fixture result="+(result==null ? "none" : result.Word));
                check(result!=null && result.Word.Equals("Curiosity",StringComparison.OrdinalIgnoreCase),"real WPS document interface reads the word at the requested point");
                check(result!=null && result.Context.Contains("learning"),"real WPS lookup retains nearby source context");
                if(GetAncestor(ScreenReader.WindowFromPoint(new Point(x,y)),2)==GetAncestor(handle,2)) {
                    CaptureResult isolated=MainForm.Probe(x,y,"uia",2800);
                    lines.Add("INFO WPS isolated probe word="+isolated.Word+" method="+isolated.Method+" error="+isolated.Error);
                    check(isolated.Word.Equals("Curiosity",StringComparison.OrdinalIgnoreCase),"real WPS full isolated hover probe locates the word");
                } else lines.Add("BLOCKED WPS fixture is covered by another window; full isolated probe not attempted");
                content=Get(document,"Content"); try { check(Convert.ToString(Get(content,"Text"))==baseline,"real WPS lookup leaves document text unchanged"); } finally { Release(content); }
            } finally {
                ScreenReader.CaptureTrace=null;
                // 仅关闭本测试新建且未被用户改动的文档。
                if(document!=null) {
                    object current=null;
                    try { current=Get(document,"Content"); if(baseline!=null && Convert.ToString(Get(current,"Text"))==baseline) Call(document,"Close",0); else lines.Add("BLOCKED test document changed; left open to preserve edits"); } catch { }
                    finally { Release(current); }
                }
                Release(window); Release(range); Release(document); Release(docs); Release(app);
            }
        }
        private static void Terminal(bool modern,Action<bool,string> check,List<string> lines)
        {
            string label=modern ? "Windows Terminal" : "PowerShell console";
            string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"TestArtifacts","capture-terminal-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string script=Path.Combine(directory,"fixture.ps1"),stop=Path.Combine(directory,"stop.txt"),title="HoverLex Capture "+Guid.NewGuid().ToString("N").Substring(0,10);
            File.WriteAllText(script,"$Host.UI.RawUI.WindowTitle='"+title+"'\r\n[Console]::WriteLine('First line.')\r\n[Console]::WriteLine('Curiosity makes learning easier.')\r\n[Console]::WriteLine('中文 context word')\r\nwhile(-not [IO.File]::Exists('"+stop.Replace("'","''")+"')) {Start-Sleep -Milliseconds 100}",new UTF8Encoding(true));
            string exe=modern ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WindowsApps","wt.exe") : "powershell.exe";
            if(modern && !File.Exists(exe)) { lines.Add("SKIP Windows Terminal not installed"); return; }
            string args=(modern ? "-w new new-tab --title \""+title+"\" powershell.exe " : "")+"-NoProfile -ExecutionPolicy Bypass -File \""+script+"\"";
            Process started=null;
            try {
                started=Process.Start(new ProcessStartInfo(exe,args) { UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden });
                IntPtr window=IntPtr.Zero; Stopwatch wait=Stopwatch.StartNew();
                while(window==IntPtr.Zero && wait.ElapsedMilliseconds<7000) {
                    EnumWindows((candidate,state)=> { var caption=new StringBuilder(512); GetWindowText(candidate,caption,512); if(caption.ToString().Contains(title)) { window=candidate; return false; } return true; },IntPtr.Zero);
                    if(window==IntPtr.Zero) { Application.DoEvents(); System.Threading.Thread.Sleep(100); }
                }
                if(window==IntPtr.Zero) { lines.Add("BLOCKED "+label+" owned fixture window not found"); return; }
                ShowOwnedFixture(window);
                AutomationElement root=AutomationElement.FromHandle(window);
                var nodes=root.FindAll(TreeScope.Subtree,new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty,true));
                TextPattern pattern=null; System.Windows.Rect[] bounds=null;
                foreach(AutomationElement node in nodes) {
                    try {
                    object value; if(!node.TryGetCurrentPattern(TextPattern.Pattern,out value)) continue;
                    var candidate=(TextPattern)value; var word=candidate.DocumentRange.FindText("Curiosity",false,true);
                    if(word==null) continue; var rectangles=word.GetBoundingRectangles(); if(rectangles.Length==0) continue;
                    pattern=candidate; bounds=rectangles; break;
                    } catch(NotSupportedException) { }
                }
                if(pattern==null) { lines.Add("BLOCKED "+label+" text pattern unavailable"); return; }
                int x=(int)(bounds[0].X+bounds[0].Width*.5),y=(int)(bounds[0].Y+bounds[0].Height*.5);
                string before=String.Join("|",pattern.GetSelection().Select(range=>range.GetText(100)));
                CaptureResult found=ScreenReader.ReadText(pattern,x,y);
                check(found!=null && found.Word.Equals("Curiosity",StringComparison.OrdinalIgnoreCase),label+" actual text range locates the pointed word");
                check(found!=null && found.Context.Contains("learning"),label+" range keeps nearby text");
                check(String.Join("|",pattern.GetSelection().Select(range=>range.GetText(100)))==before,label+" lookup does not change selection or execute a command");
                bool exposed=GetAncestor(ScreenReader.WindowFromPoint(new Point(x,y)),2)==window;
                if(exposed) {
                    CaptureResult full=MainForm.Probe(x,y,"uia",3500);
                    lines.Add("INFO "+label+" isolated probe word="+full.Word+" method="+full.Method+" error="+full.Error);
                    check(full.Word.Equals("Curiosity",StringComparison.OrdinalIgnoreCase),label+" full isolated hover probe locates the word");
                } else lines.Add("BLOCKED "+label+" full mouse probe obscured by another window or lock screen");
            } finally { File.WriteAllText(stop,"stop"); if(started!=null) started.Dispose(); }
        }
    }
}
