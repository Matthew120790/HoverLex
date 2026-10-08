using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;

namespace HoverLex
{
    internal static class HoverRuntimeTests
    {
        private delegate bool Each(IntPtr window,IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumWindows(Each callback,IntPtr state);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr window,Each callback,IntPtr state);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr window,StringBuilder text,int count);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window,uint flags);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint first,uint second,bool attach);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr window,StringBuilder text,int count);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="SendMessageTimeoutW")] private static extern IntPtr ReadLabel(IntPtr window,uint message,IntPtr count,StringBuilder text,uint flags,uint timeout,out IntPtr result);
        [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Key,Scan; public uint Flags,Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Explicit,Size=32)] private struct Union { [FieldOffset(0)] public Keyboard Key; }
        [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public Union Value; }
        [DllImport("user32.dll")] private static extern uint SendInput(uint count,Input[] input,int size);
        private static void ControlKey(bool up)
        {
            Input[] input={new Input { Type=1,Value=new Union { Key=new Keyboard { Key=0x11,Flags=up ? 2u : 0u } } }};
            if(SendInput(1,input,Marshal.SizeOf(typeof(Input)))!=1) throw new InvalidOperationException("test Ctrl input was rejected");
        }
        private static void Focus(IntPtr window)
        {
            ShowWindow(window,9); uint pid,thread=GetCurrentThreadId(),other=GetWindowThreadProcessId(GetForegroundWindow(),out pid);
            bool attached=other!=0 && other!=thread && AttachThreadInput(thread,other,true);
            try { SetForegroundWindow(window); } finally { if(attached) AttachThreadInput(thread,other,false); }
            Thread.Sleep(300);
        }
        private static IntPtr FindDemo(int pid)
        {
            IntPtr result=IntPtr.Zero;
            EnumWindows((window,state)=> { uint owner; GetWindowThreadProcessId(window,out owner); if(owner!=pid) return true; var text=new StringBuilder(100); GetWindowText(window,text,100); if(text.ToString()=="取词练习 · Ctrl + 鼠标") result=window; return true; },IntPtr.Zero);
            return result;
        }
        private static bool HasWordCard(int pid)
        {
            bool found=false;
            EnumWindows((window,state)=> {
                uint owner; GetWindowThreadProcessId(window,out owner); if(owner!=pid || !IsWindowVisible(window)) return true;
                EnumChildWindows(window,(child,arg)=> { var kind=new StringBuilder(256); GetClassName(child,kind,256); if(kind.ToString().IndexOf("STATIC",StringComparison.OrdinalIgnoreCase)<0) return true; var text=new StringBuilder(150); IntPtr value; ReadLabel(child,13,new IntPtr(text.Capacity),text,2,100,out value); if(text.ToString().Equals("Curiosity",StringComparison.OrdinalIgnoreCase)) { found=true; return false; } return true; },IntPtr.Zero);
                return !found;
            },IntPtr.Zero);
            return found;
        }
        private static bool Hover(IntPtr demo,int pid,Point point,List<string> lines,string name)
        {
            Focus(demo); Cursor.Position=point;
            if(GetForegroundWindow()!=demo || GetAncestor(ScreenReader.WindowFromPoint(point),2)!=demo) { lines.Add("BLOCKED "+name+" fixture lacks foreground focus; no Ctrl sent"); return false; }
            bool shown=false;
            var selected=InputReader.ReadSelection(InputReader.SelectionFocusWindow().ToInt64(),point.X,point.Y);
            lines.Add("INFO "+name+" selectedLength="+selected.Text.Length);
            try {
                ControlKey(false); Thread.Sleep(120);
                lines.Add("INFO "+name+" ctrlOnly="+Native.CtrlOnly());
                Stopwatch wait=Stopwatch.StartNew();
                while(wait.ElapsedMilliseconds<5000) {
                    uint foregroundPid; GetWindowThreadProcessId(GetForegroundWindow(),out foregroundPid);
                    if(foregroundPid!=pid) { lines.Add("INFO "+name+" foreground left the tested app"); break; }
                    if(HasWordCard(pid)) { shown=true; break; } Thread.Sleep(100);
                }
            } finally { ControlKey(true); }
            lines.Add((shown ? "PASS " : "FAIL ")+name+" installed Ctrl hover displays Curiosity");
            return shown;
        }
        private static async Task<bool> Wait(Func<bool> condition,int milliseconds=5000)
        {
            Stopwatch wait=Stopwatch.StartNew(); while(wait.ElapsedMilliseconds<milliseconds) { if(condition()) return true; await Task.Delay(40); } return condition();
        }
        public static int Controller()
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            List<string> lines=new List<string>(); int failures=0,calls=0;
            Action<bool,string> check=(ok,name)=> { lines.Add((ok ? "PASS " : "FAIL ")+name); if(!ok) failures++; };
            string originalRoot=Environment.GetEnvironmentVariable("HOVERLEX_INSTALL_ROOT");
            string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"TestArtifacts","hover-controller-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory,"UserData"));
            new Settings { Delay=150,EnglishCorrection=false,ReviewReminder=false }.Save(Path.Combine(directory,"UserData","settings.json"));
            Environment.SetEnvironmentVariable("HOVERLEX_INSTALL_ROOT",directory);
            Point previous=Cursor.Position,point=previous;
            try {
                using(var form=new MainForm())
                using(var sample=new TextBox { ReadOnly=true,Multiline=true,Text="Curiosity makes learning easier.",Location=new Point(210,130),Size=new Size(650,150),Font=new Font("Segoe UI",24),TabStop=false }) {
                    var old=(SelectionTranslationController)typeof(MainForm).GetField("selectionTranslation",flags).GetValue(form); old.Dispose();
                    var selection=new SelectionTranslationController(message=>{},(text,direction,token)=> { calls++; return Task.FromResult("好奇心让学习更轻松。"); }); selection.Enabled=true;
                    selection.PointLookup=(p,f)=>(Task)typeof(MainForm).GetMethod("LookupTappedPoint",flags).Invoke(form,new object[] { p,f });
                    selection.Trace=message=>lines.Add("TRACE "+message);
                    typeof(MainForm).GetField("selectionTranslation",flags).SetValue(form,selection);
                    var popup=(LookupPanel)typeof(MainForm).GetField("popup",flags).GetValue(form);
                    popup.FocusTrace=message=>lines.Add("TRACE popup "+message);
                    var enabled=(CheckBox)typeof(MainForm).GetField("enabled",flags).GetValue(form);
                    form.Controls.Add(sample); sample.BringToFront();
                    form.Shown+=async delegate {
                        try {
                            bool ready=await Wait(()=>typeof(MainForm).GetField("dictionary",flags).GetValue(form)!=null);
                            check(ready,"actual main controller loads the real dictionary");
                            form.TopMost=true; Focus(form.Handle); sample.Focus(); sample.SelectAll(); await Task.Delay(150);
                            var pattern=(TextPattern)AutomationElement.FromHandle(sample.Handle).GetCurrentPattern(TextPattern.Pattern);
                            var bounds=pattern.DocumentRange.FindText("Curiosity",false,false).GetBoundingRectangles()[0];
                            point=new Point((int)(bounds.X+bounds.Width/2),(int)(bounds.Y+bounds.Height/2)); Cursor.Position=point;
                            if(GetForegroundWindow()!=form.Handle || GetAncestor(ScreenReader.WindowFromPoint(point),2)!=form.Handle) throw new InvalidOperationException("own fixture lost foreground focus; no Ctrl sent");
                            ControlKey(false);
                            bool shown=await Wait(()=>popup.Visible && HasWordCard(Process.GetCurrentProcess().Id));
                            check(shown,"holding Ctrl over preselected text runs dwell, isolated reader, dictionary and word card");
                            check(InputReader.FocusWindow()==sample.Handle,"word card preserves the source editor keyboard focus");
                            check(GetAncestor(ScreenReader.WindowFromPoint(popup.PointToScreen(new Point(30,30))),2)==popup.Handle,"word card stays above the reading window after restoring focus");
                            lines.Add("INFO expectedFocus="+sample.Handle+" actualFocus="+InputReader.FocusWindow()+" main="+form.Handle+" popup="+popup.Handle);
                            ControlKey(true); await Task.Delay(300);
                            check(calls==0 && !selection.Panel.IsShown,"releasing Ctrl after hover does not append sentence translation");
                            enabled.Checked=false; await Task.Delay(100); ControlKey(false); await Task.Delay(700);
                            check(!popup.Visible && calls==0,"disabled capture prevents both word lookup and selection translation");
                            ControlKey(true); enabled.Checked=true; await Task.Delay(100); ControlKey(false);
                            shown=await Wait(()=>popup.Visible && HasWordCard(Process.GetCurrentProcess().Id));
                            check(shown,"turning capture on restores Ctrl hover at the same point");
                            ControlKey(true); await Task.Delay(200);
                            ControlKey(false); await Task.Delay(45); ControlKey(true);
                            bool translated=await Wait(()=>selection.Panel.IsShown && selection.Panel.English=="好奇心让学习更轻松。");
                            check(translated && calls==1,"a short Ctrl tap still translates the selected sentence");
                            selection.Dismiss(); typeof(MainForm).GetMethod("DismissHoverLookup",flags).Invoke(form,null);
                            Focus(form.Handle); sample.Focus(); sample.SelectionLength=0; sample.SelectionStart=sample.TextLength; Cursor.Position=point;
                            await Task.Delay(100); ControlKey(false); await Task.Delay(45); ControlKey(true);
                            shown=await Wait(()=>popup.Visible && HasWordCard(Process.GetCurrentProcess().Id));
                            check(shown && calls==1,"a short Ctrl tap without selection captures the pointed word and never translates the draft");
                            check(InputReader.FocusWindow()==sample.Handle,"tap lookup preserves the source keyboard focus");
                            check(sample.Text=="Curiosity makes learning easier.","hover and selection workflows leave the readonly source unchanged");
                            var persisted=Settings.Load(Path.Combine(directory,"UserData","settings.json"));
                            check(persisted.Enabled && persisted.Delay==150,"capture toggle persists in isolated test settings");
                        } catch(Exception error) { lines.Add("BLOCKED "+error.Message); failures++; }
                        finally { ControlKey(true); typeof(MainForm).GetMethod("Quit",flags).Invoke(form,null); }
                    };
                    Application.Run(form);
                }
            } finally {
                Environment.SetEnvironmentVariable("HOVERLEX_INSTALL_ROOT",originalRoot);
                if(Cursor.Position==point) Cursor.Position=previous;
                File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"hover-controller-tests.txt"),lines,Encoding.UTF8);
            }
            return failures==0 ? 0 : 1;
        }
        public static int Installed()
        {
            List<string> lines=new List<string>(); IntPtr demo=IntPtr.Zero; bool created=false; TogglePattern toggle=null; Point previous=Cursor.Position,point=previous;
            try {
                string install=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HoverLex");
                var current=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(install,"current.json")));
                string path=Path.GetFullPath(Path.Combine(install,(string)current["Directory"],"HoverLex.exe"));
                Process process=Process.GetProcessesByName("HoverLex").First(candidate=>candidate.MainModule.FileName.Equals(path,StringComparison.OrdinalIgnoreCase) && candidate.MainWindowHandle!=IntPtr.Zero);
                int pid=process.Id; IntPtr main=process.MainWindowHandle; process.Dispose();
                var root=AutomationElement.FromHandle(main);
                if(!root.Current.IsEnabled) throw new InvalidOperationException("main window has a modal dialog; no action taken");
                Focus(main); root=AutomationElement.FromHandle(main);
                var switchNode=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"captureEnabled"));
                if(switchNode==null) throw new InvalidOperationException("capture switch not exposed; no action taken");
                toggle=(TogglePattern)switchNode.GetCurrentPattern(TogglePattern.Pattern);
                if(toggle.Current.ToggleState!=ToggleState.On) throw new InvalidOperationException("capture is disabled; original preference retained");
                demo=FindDemo(pid);
                if(demo==IntPtr.Zero) {
                    var button=root.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,"体验取词"));
                    if(button==null) throw new InvalidOperationException("demo button was not exposed");
                    ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); Thread.Sleep(300); demo=FindDemo(pid); created=true;
                }
                if(demo==IntPtr.Zero) throw new InvalidOperationException("demo window was not created");
                Focus(demo);
                var editor=AutomationElement.FromHandle(demo).FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
                if(editor==null) EnumChildWindows(demo,(child,state)=> { var kind=new StringBuilder(256); GetClassName(child,kind,256); if(kind.ToString().IndexOf("EDIT",StringComparison.OrdinalIgnoreCase)>=0) { editor=AutomationElement.FromHandle(child); return false; } return true; },IntPtr.Zero);
                if(editor==null) throw new InvalidOperationException("readonly demo editor was not exposed");
                var text=(TextPattern)editor.GetCurrentPattern(TextPattern.Pattern);
                string source=text.DocumentRange.GetText(-1).Replace("\r","").Trim();
                if(source!="Curiosity makes learning easier.\nRead a little every day.\nUnderstand each word in context.") throw new InvalidOperationException("demo text differs; no key sent");
                var rectangles=text.DocumentRange.FindText("Curiosity",false,false).GetBoundingRectangles();
                point=new Point((int)(rectangles[0].X+rectangles[0].Width/2),(int)(rectangles[0].Y+rectangles[0].Height/2));
                Hover(demo,pid,point,lines,"before reset");
                toggle.Toggle(); Thread.Sleep(250); toggle.Toggle(); Thread.Sleep(250);
                Hover(demo,pid,point,lines,"after disabling and enabling");
            } catch(Exception error) { lines.Add("BLOCKED "+error.Message); }
            finally {
                if(toggle!=null && toggle.Current.ToggleState!=ToggleState.On) try { toggle.Toggle(); } catch { }
                if(Cursor.Position==point) Cursor.Position=previous;
                if(created && demo!=IntPtr.Zero) try { ((WindowPattern)AutomationElement.FromHandle(demo).GetCurrentPattern(WindowPattern.Pattern)).Close(); } catch { }
                File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"hover-runtime-tests.txt"),lines,Encoding.UTF8);
            }
            return lines.Any(line=>line.StartsWith("FAIL") || line.StartsWith("BLOCKED")) ? 1 : 0;
        }
    }
}
