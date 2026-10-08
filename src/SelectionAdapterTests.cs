using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace HoverLex
{
    internal static class SelectionAdapterTests
    {
        private const string Sentence="Curiosity makes learning easier.";
        private delegate bool EnumWindow(IntPtr window,IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback,IntPtr state);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder name,int length);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window,EnumWindow callback,IntPtr state);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window,out NativeRect rect);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from,uint to,bool attach);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr PostMessage(IntPtr window,uint message,IntPtr value,IntPtr data);
        [StructLayout(LayoutKind.Sequential)] private struct Mouse { public int X,Y; public uint Data,Flags,Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] private struct Union { [FieldOffset(0)] public Mouse Mouse; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Value; }
        [DllImport("user32.dll")] private static extern uint SendInput(uint count,Input[] values,int size);
        private static IntPtr FindWindow(string title)
        {
            IntPtr found=IntPtr.Zero;
            EnumWindows((window,state)=> { StringBuilder text=new StringBuilder(500),kind=new StringBuilder(200); GetWindowText(window,text,500); GetClassName(window,kind,200); if(text.ToString().Contains(title) && (kind.ToString()=="Chrome_WidgetWin_1" || kind.ToString()=="CASCADIA_HOSTING_WINDOW_CLASS" || kind.ToString()=="ConsoleWindowClass")) { if(found==IntPtr.Zero || IsWindowVisible(window)) found=window; } return true; },IntPtr.Zero);
            return found;
        }
        private static async Task<bool> Wait(Func<bool> condition,int timeout=8000)
        {
            Stopwatch time=Stopwatch.StartNew(); while(time.ElapsedMilliseconds<timeout) { if(condition()) return true; await Task.Delay(80); } return condition();
        }
        private static System.Windows.Automation.Text.TextPatternRange SentenceRange(TextPattern pattern)
        {
            try { return pattern.DocumentRange.FindText(Sentence,false,false); }
            catch(NotSupportedException) {
                var line=pattern.DocumentRange.Clone();
                line.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.End,line,System.Windows.Automation.Text.TextPatternRangeEndpoint.Start);
                line.ExpandToEnclosingUnit(System.Windows.Automation.Text.TextUnit.Line);
                for(int n=0;n<200;n++) {
                    string text=line.GetText(-1); int index=text.IndexOf(Sentence,StringComparison.Ordinal);
                    if(index>=0) {
                        line.MoveEndpointByRange(System.Windows.Automation.Text.TextPatternRangeEndpoint.End,line,System.Windows.Automation.Text.TextPatternRangeEndpoint.Start);
                        line.MoveEndpointByUnit(System.Windows.Automation.Text.TextPatternRangeEndpoint.Start,System.Windows.Automation.Text.TextUnit.Character,index);
                        line.MoveEndpointByUnit(System.Windows.Automation.Text.TextPatternRangeEndpoint.End,System.Windows.Automation.Text.TextUnit.Character,Sentence.Length);
                        return line;
                    }
                    if(line.Move(System.Windows.Automation.Text.TextUnit.Line,1)!=1) break;
                }
                return null;
            }
        }
        private static void MouseAt(IntPtr window,Point point,uint action)
        {
            if(GetForegroundWindow()!=window || GetAncestor(WindowFromPoint(point),2)!=window) throw new InvalidOperationException("owned selection fixture lost focus; no mouse action sent; expected="+window+" foreground="+GetForegroundWindow()+" point="+point+" pointRoot="+GetAncestor(WindowFromPoint(point),2));
            Rectangle screen=SystemInformation.VirtualScreen;
            Input[] values={ new Input { Type=0,Value=new Union { Mouse=new Mouse { X=(point.X-screen.Left)*65535/Math.Max(1,screen.Width-1),Y=(point.Y-screen.Top)*65535/Math.Max(1,screen.Height-1),Flags=0xc001|action } } } };
            if(SendInput(1,values,Marshal.SizeOf(typeof(Input)))!=1) throw new InvalidOperationException("mouse test input was rejected");
        }
        private static void ReleaseMouse() { Input[] values={ new Input { Value=new Union { Mouse=new Mouse { Flags=4 } } } }; SendInput(1,values,Marshal.SizeOf(typeof(Input))); }
        private static async Task Exercise(IntPtr window,string label,Action<bool,string> check,List<string> lines,string directory)
        {
            for(int attempt=0;attempt<12;attempt++) {
                ShowWindow(window,9);
                uint pid,thread=GetCurrentThreadId(),other=GetWindowThreadProcessId(GetForegroundWindow(),out pid);
                bool attached=other!=0 && other!=thread && AttachThreadInput(thread,other,true);
                try { SetForegroundWindow(window); } finally { if(attached) AttachThreadInput(thread,other,false); }
                await Task.Delay(150);
                if(GetForegroundWindow()==window) break;
            }
            bool interactive=GetForegroundWindow()==window;
            if(interactive && label=="Cursor terminal") {
                NativeRect rect; GetWindowRect(window,out rect);
                using(Bitmap picture=new Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top)) using(Graphics graphics=Graphics.FromImage(picture)) {graphics.CopyFromScreen(rect.Left,rect.Top,0,0,picture.Size); picture.Save(Path.Combine(directory,"editor-initial.png"));}
                var all=AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants,Condition.TrueCondition);
                foreach(AutomationElement node in all) if(node.Current.Name=="Cursor’s AI features require you to be logged in") {lines.Add("BLOCKED Cursor isolated fixture is covered by its login screen; integrated terminal was not tested"); return;}
            }
            if(!interactive) { StringBuilder kind=new StringBuilder(200); GetClassName(window,kind,200); lines.Add("BLOCKED "+label+" fixture cannot receive focus; class="+kind+" visible="+IsWindowVisible(window)+" window="+window+" foreground="+GetForegroundWindow()); }
            AutomationElement root=AutomationElement.FromHandle(window),chosen=null; TextPattern pattern=null;
            for(int attempt=0;attempt<25 && pattern==null;attempt++) {
                AutomationElementCollection nodes=root.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.IsTextPatternAvailableProperty,true));
                foreach(AutomationElement node in nodes) {
                    object value; if(!node.TryGetCurrentPattern(TextPattern.Pattern,out value)) continue;
                    TextPattern candidate=(TextPattern)value;
                    string content=candidate.DocumentRange.GetText(-1);
                    if(content.Contains(Sentence)) { chosen=node; pattern=candidate; lines.Add("INFO "+label+" fixture provider="+node.Current.ControlType.ProgrammaticName+" class="+node.Current.ClassName); break; }
                }
                if(pattern==null) await Task.Delay(200);
            }
            if(pattern==null && !interactive && (label=="Chrome" || label=="Edge")) {
                InputSnapshot renderer=InputReader.ReadBrowserRendererSelection(window.ToInt64(),0,0);
                if(renderer==null) lines.Add("BLOCKED "+label+" locked desktop does not expose webpage selection");
                else check(renderer.Text.Trim()==Sentence,label+" browser renderer reads selected webpage without relying on native focus");
                if(renderer!=null) lines.Add("INFO "+label+" renderer="+renderer.Id+" selected="+renderer.Text);
                return;
            }
            if(pattern==null) { check(false,label+" exposes a document text pattern"); return; }
            if(!interactive) {
                if(label!="Chrome" && label!="Edge") {
                    var selectedRange=SentenceRange(pattern);
                    if(selectedRange==null) { check(false,label+" fixture sentence range found"); return; }
                    selectedRange.Select();
                }
                string text=""; foreach(var part in pattern.GetSelection()) text+=part.GetText(2001);
                lines.Add("INFO "+label+" locked-desktop provider selected="+text);
                InputSnapshot actual=InputReader.SelectionFromElement(chosen,window.ToInt64());
                check(actual!=null && actual.Text.Trim()==Sentence,label+" real provider reads programmatic selection on locked desktop");
                return;
            }
            Point start,end;
            bool browser=label=="Chrome" || label=="Edge";
            if(browser) {
                var doc=chosen.Current.BoundingRectangle; float scale=GetDpiForWindow(window)/96f; float width;
                NativeRect native;
                EnumChildWindows(window,(child,state)=> { StringBuilder kind=new StringBuilder(200); GetClassName(child,kind,200); if(kind.ToString()=="Chrome_RenderWidgetHostHWND" && IsWindowVisible(child) && GetWindowRect(child,out native)) { doc=new System.Windows.Rect(native.Left,native.Top,native.Right-native.Left,native.Bottom-native.Top); return false; } return true; },IntPtr.Zero);
                using(Graphics graphics=Graphics.FromHwnd(window)) using(Font font=new Font("Consolas",24*scale,GraphicsUnit.Pixel)) width=graphics.MeasureString(Sentence,font,Int32.MaxValue,StringFormat.GenericTypographic).Width;
                start=new Point((int)(doc.Left+80*scale)+1,(int)(doc.Top+154*scale)); end=new Point((int)(doc.Left+80*scale+width)+1,start.Y);
                NativeRect outer; GetWindowRect(window,out outer); lines.Add("INFO "+label+" outer="+outer.Left+","+outer.Top+","+outer.Right+","+outer.Bottom+" viewport="+doc+" dpi="+scale+" drag="+start+".."+end);
            } else {
                var range=SentenceRange(pattern); var rectangles=range==null ? new System.Windows.Rect[0] : range.GetBoundingRectangles();
                if(rectangles.Length==0) { lines.Add("BLOCKED "+label+" fixture text has no screen bounds"); return; }
                var rect=rectangles[0]; start=new Point((int)Math.Floor(rect.Left)+1,(int)(rect.Top+rect.Height/2)); end=new Point((int)Math.Ceiling(rect.Right)+1,start.Y);
            }
            using(SelectionTranslationController controller=new SelectionTranslationController(message=>lines.Add("INFO "+label+" "+message),(text,direction,token)=>Task.FromResult("好奇心让学习更轻松。"))) {
                controller.EnglishCorrection=false; controller.Enabled=true;
                NativeRect bounds; GetWindowRect(window,out bounds);
                using(Bitmap screen=new Bitmap(bounds.Right-bounds.Left,bounds.Bottom-bounds.Top)) using(Graphics graphics=Graphics.FromImage(screen)) { graphics.CopyFromScreen(bounds.Left,bounds.Top,0,0,screen.Size); screen.Save(Path.Combine(directory,label.Replace(' ','-')+"-fixture.png")); }
                lines.Add("INFO "+label+" mouse="+start+".."+end);
                MouseAt(window,start,0); MouseAt(window,start,2);
                try { for(int step=1;step<=12;step++) { MouseAt(window,new Point(start.X+(end.X-start.X)*step/12,start.Y),0); await Task.Delay(20); } }
                finally { if(GetForegroundWindow()==window && GetAncestor(WindowFromPoint(end),2)==window) MouseAt(window,end,4); else ReleaseMouse(); }
                await Task.Delay(250);
                string selected=""; foreach(var part in pattern.GetSelection()) selected+=part.GetText(2001);
                lines.Add("INFO "+label+" provider="+chosen.Current.ClassName+" selected="+selected+" focus="+InputReader.FocusWindow());
                check(selected.Trim()==Sentence,label+" actual mouse drag selects the sentence");
                IntPtr focus=InputReader.SelectionFocusWindow();
                InputSnapshot snapshot=await SelectionTranslationController.ReadAsync(focus.ToInt64(),end,CancellationToken.None);
                lines.Add("INFO "+label+" reader id="+snapshot.Id+" error="+snapshot.Error+" text="+snapshot.Text);
                check(snapshot.Text.Trim()==Sentence,label+" isolated selection reader retrieves the sentence");
                if(browser) {
                    bool hadText=Clipboard.ContainsText(); string oldClipboard=hadText ? Clipboard.GetText() : "";
                    InputSnapshot compatible=await SelectionTranslationController.ReadAsync(focus.ToInt64(),end,CancellationToken.None,true);
                    check(compatible.Text.Trim()==Sentence,label+" compatibility reader gets the selected sentence: "+compatible.Error);
                    check(Clipboard.ContainsText()==hadText && (!hadText || Clipboard.GetText()==oldClipboard),label+" compatibility reader restores the original clipboard text");
                }
                TranslationTests.KeyOwnedWindow(window,InputReader.FocusWindow(),0x11);
                bool shown=await Wait(()=>controller.Panel.IsShown && controller.Panel.English=="好奇心让学习更轻松。");
                check(shown,label+" mouse selection plus Ctrl displays Chinese translation");
                string after=""; foreach(var part in pattern.GetSelection()) after+=part.GetText(2001);
                check(after.Trim()==Sentence,label+" translation preserves the selection");
                if(shown) using(Bitmap image=new Bitmap(controller.Panel.Width,controller.Panel.Height)) { controller.Panel.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(Path.Combine(directory,label.Replace(' ','-')+".png")); }
            }
        }
        private static async Task Browser(string executable,string label,Action<bool,string> check,List<string> lines,string directory)
        {
            if(!File.Exists(executable)) { lines.Add("BLOCKED "+label+" is not installed"); return; }
            string title="HoverLex selected webpage "+Guid.NewGuid().ToString("N"),page=Path.Combine(directory,label+".html"),profile=Path.Combine(directory,label+"-profile");
            File.WriteAllText(page,"<!doctype html><title>"+title+"</title><body style='margin:0;font:24px Consolas'><p style='position:absolute;left:80px;top:140px;margin:0;white-space:pre'>"+Sentence+"</p><script>setTimeout(()=>{let r=document.createRange();r.selectNodeContents(document.querySelector('p'));let s=getSelection();s.removeAllRanges();s.addRange(r)},100)</script></body>");
            string args="--user-data-dir=\""+profile+"\" --no-first-run --no-default-browser-check --disable-features=msEdgeSidebarV2 --app=\""+new Uri(page).AbsoluteUri+"\"";
            IntPtr window=IntPtr.Zero;
            using(Process process=Process.Start(new ProcessStartInfo(executable,args) { UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden })) try {
                await Wait(()=> { window=FindWindow(title); return window!=IntPtr.Zero; },18000);
                if(window==IntPtr.Zero) { lines.Add("BLOCKED "+label+" own fixture window not found"); return; }
                await Task.Delay(800); await Exercise(window,label,check,lines,directory);
            } finally { if(window!=IntPtr.Zero) PostMessage(window,0x10,IntPtr.Zero,IntPtr.Zero); }
        }
        private static async Task Terminal(bool modern,Action<bool,string> check,List<string> lines,string directory)
        {
            string title="HoverLex selected terminal "+Guid.NewGuid().ToString("N"),stop=Path.Combine(directory,title+".stop"),script=Path.Combine(directory,title+".ps1");
            string quickEdit=modern ? "" : "Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public class SelectionConsole { [DllImport(\"kernel32.dll\")] public static extern IntPtr GetStdHandle(int h); [DllImport(\"kernel32.dll\")] public static extern bool GetConsoleMode(IntPtr h,out uint m); [DllImport(\"kernel32.dll\")] public static extern bool SetConsoleMode(IntPtr h,uint m); }'\r\n$handle=[SelectionConsole]::GetStdHandle(-10); [uint32]$mode=0; [void][SelectionConsole]::GetConsoleMode($handle,[ref]$mode); [void][SelectionConsole]::SetConsoleMode($handle,($mode -bor 0xC0))\r\n";
            File.WriteAllText(script,"$Host.UI.RawUI.WindowTitle='"+title+"'\r\n"+quickEdit+"[Console]::WriteLine('First line.')\r\n[Console]::WriteLine('"+Sentence+"')\r\n[Console]::WriteLine('Last line.')\r\nwhile(-not [IO.File]::Exists('"+stop.Replace("'","''")+"')) {Start-Sleep -Milliseconds 100}",new UTF8Encoding(true));
            string executable=modern ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft/WindowsApps/wt.exe") : "powershell.exe";
            string args=(modern ? "-w new new-tab --title \""+title+"\" powershell.exe " : "")+"-NoProfile -ExecutionPolicy Bypass -File \""+script+"\"";
            string label=modern ? "Windows Terminal" : "PowerShell console"; IntPtr window=IntPtr.Zero;
            using(Process process=Process.Start(new ProcessStartInfo(executable,args) { UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden })) try {
                await Wait(()=> { window=FindWindow(title); return window!=IntPtr.Zero; },12000);
                if(window==IntPtr.Zero) { lines.Add("BLOCKED "+label+" own fixture window not found"); return; }
                await Exercise(window,label,check,lines,directory);
            } finally { File.WriteAllText(stop,""); }
        }
        private static async Task Editor(Action<bool,string> check,List<string> lines,string directory)
        {
            string executable=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs/cursor/Cursor.exe");
            if(!File.Exists(executable)) { lines.Add("BLOCKED Cursor is not installed"); return; }
            string title="HoverLex terminal QA "+Guid.NewGuid().ToString("N"),workspace=Path.Combine(directory,"editor-workspace"),profile=Path.Combine(directory,"editor-profile"),stop=Path.Combine(directory,"editor.stop"),script=Path.Combine(directory,"editor.ps1");
            Directory.CreateDirectory(Path.Combine(workspace,".vscode")); Directory.CreateDirectory(Path.Combine(profile,"User"));
            var json=new System.Web.Script.Serialization.JavaScriptSerializer();
            File.WriteAllText(script,"[Console]::WriteLine('First line.')\r\n[Console]::WriteLine('"+Sentence+"')\r\n[Console]::WriteLine('Last line.')\r\nwhile(-not [IO.File]::Exists('"+stop.Replace("'","''")+"')) {Start-Sleep -Milliseconds 100}",new UTF8Encoding(true));
            File.WriteAllText(Path.Combine(profile,"User","settings.json"),json.Serialize(new Dictionary<string,object> {
                {"window.title",title},{"workbench.startupEditor","none"},{"task.allowAutomaticTasks","on"},
                {"security.workspace.trust.enabled",false},{"terminal.integrated.shellIntegration.enabled",false}
            }));
            File.WriteAllText(Path.Combine(workspace,".vscode","tasks.json"),json.Serialize(new { version="2.0.0",tasks=new[] { new { label="HoverLex selection fixture",type="process",command="powershell.exe",args=new[] {"-NoProfile","-ExecutionPolicy","Bypass","-File",script},runOptions=new {runOn="folderOpen"},presentation=new {reveal="always",focus=true},problemMatcher=new string[0] } } }));
            IntPtr window=IntPtr.Zero;
            using(Process process=Process.Start(new ProcessStartInfo(executable,"--user-data-dir \""+profile+"\" --extensions-dir \""+Path.Combine(directory,"editor-extensions")+"\" --disable-extensions --disable-workspace-trust --new-window \""+workspace+"\"") {UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden})) try {
                await Wait(()=> {window=FindWindow(title); return window!=IntPtr.Zero;},25000);
                if(window==IntPtr.Zero) {lines.Add("BLOCKED Cursor own fixture window not found"); return;}
                await Task.Delay(7000); await Exercise(window,"Cursor terminal",check,lines,directory);
            } finally {File.WriteAllText(stop,""); if(window!=IntPtr.Zero) PostMessage(window,0x10,IntPtr.Zero,IntPtr.Zero);}
        }
        public static int Run(string only="")
        {
            List<string> lines=new List<string>(); int exit=0;
            Action<bool,string> check=(pass,name)=> { lines.Add((pass ? "PASS " : "FAIL ")+name); if(!pass) exit=1; };
            string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"TestArtifacts","selection-adapters-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(directory);
            using(Form runner=new Form { Text="HoverLex selection adapter test",Size=new Size(300,150),ShowInTaskbar=false }) {
                runner.Shown+=async delegate {
                    try {
                        Func<Task>[] cases={
                            ()=>Browser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Google/Chrome/Application/chrome.exe"),"Chrome",check,lines,directory),
                            ()=>Browser(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Microsoft/Edge/Application/msedge.exe"),"Edge",check,lines,directory),
                            ()=>Terminal(true,check,lines,directory),()=>Terminal(false,check,lines,directory),()=>Editor(check,lines,directory)
                        };
                        string[] names={ "chrome","edge","terminal","console","editor" };
                        for(int index=0;index<cases.Length;index++) { if(only.Length>0 && only!=names[index]) continue; try { await cases[index](); } catch(Exception error) { lines.Add("FAIL "+error); exit=1; } File.WriteAllLines(Path.Combine(directory,"results.txt"),lines); }
                    } catch(Exception error) { lines.Add("FAIL "+error); exit=1; }
                    finally { runner.Close(); }
                };
                Application.Run(runner);
            }
            File.WriteAllLines(Path.Combine(directory,"results.txt"),lines);
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selection-adapter-tests.txt"),lines);
            if(exit==0 && lines.Exists(line=>line.StartsWith("BLOCKED ",StringComparison.Ordinal))) exit=3;
            return exit;
        }
    }
}
