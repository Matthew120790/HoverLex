using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace HoverLex
{
    public static class SpeechTests
    {
        private sealed class Player : IEnglishSpeech
        {
            public event Action Changed;
            public bool IsSpeaking { get; private set; }
            public string Error { get { return ""; } }
            public string Last="",Failure="";
            public int Plays,Stops;
            public string Play(string text) { if(Failure.Length>0) return Failure; Plays++; Last=text; IsSpeaking=true; if(Changed!=null) Changed(); return ""; }
            public void Stop() { if(IsSpeaking) Stops++; IsSpeaking=false; if(Changed!=null) Changed(); }
            public void Dispose() { Stop(); Changed=null; }
        }
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr value,IntPtr data);
        private static IEnumerable<Control> All(Control root) { foreach(Control child in root.Controls) { yield return child; foreach(Control nested in All(child)) yield return nested; } }
        private static void Wait(Func<bool> done,int timeout) { Stopwatch watch=Stopwatch.StartNew(); while(!done() && watch.ElapsedMilliseconds<timeout) { Application.DoEvents(); System.Threading.Thread.Sleep(10); } }
        private static void Click(Button button) { if(button.Enabled) { SendMessage(button.Handle,0x201,new IntPtr(1),new IntPtr(0x000a000a)); SendMessage(button.Handle,0x202,IntPtr.Zero,new IntPtr(0x000a000a)); } }
        public static int Run()
        {
            List<string> lines=new List<string>(); Action<bool,string> check=(ok,text)=>lines.Add((ok ? "PASS " : "FAIL ")+text);
            try {
                using(Player player=new Player()) using(TranslationPanel panel=new TranslationPanel(player)) {
                    InputSnapshot source=new InputSnapshot { Text="你好",X=200,Y=200,Width=100,Height=30 };
                    Button read=(Button)panel.Controls.Find("speakTranslation",true)[0];
                    panel.Pending(source); panel.ShowNear(source); check(!read.Enabled,"pending translations cannot pronounce loading text");
                    panel.Result(source,"Hello, world."); check(read.Enabled && player.Plays==0,"completed translation enables pronunciation without automatic audio");
                    Click(read); check(player.Last=="Hello, world." && player.Plays==1 && read.Text=="停止","pronunciation reads the complete English translation and offers stop");
                    Click(read); check(!player.IsSpeaking && player.Stops==1 && read.Text=="朗读","the second click stops playback");
                    Click(read); panel.Pending(source); check(!player.IsSpeaking && !read.Enabled,"a new translation stops the previous audio");
                    panel.Failure("测试错误"); Click(read); check(!read.Enabled && player.Plays==2,"translation errors are never pronounced as English");
                    panel.Result(source,"Hello again."); panel.Replaced(true);
                    check(read.Enabled && !All(panel).OfType<Button>().First(b=>b.Text=="替换").Enabled && !All(panel).OfType<Button>().First(b=>b.Text=="重译").Enabled,"automatic replacement leaves a playable receipt and disables repeated replacement");
                    Click(read); panel.Hide(); check(!player.IsSpeaking,"closing the pronunciation card stops audio");
                    int hiddenPlays=player.Plays; Click(read); check(player.Plays==hiddenPlays,"a hidden card cannot restart stale pronunciation");
                    panel.Result(source,"Hello again."); panel.ShowNear(source);
                    player.Failure="请安装英语语音"; Click(read);
                    check(All(panel).Any(c=>c.Text==player.Failure),"missing voice errors remain visible on the card"); player.Failure="";
                    panel.Result(source,"Hello, this is the translated sentence.");
                    using(Form fixture=new Form { Text="HoverLex speech focus test",ClientSize=new Size(600,280) })
                    using(TextBox editor=new TextBox { Text="输入保持不变",Bounds=new Rectangle(20,20,400,40) }) {
                        fixture.Controls.Add(editor); fixture.Show(); ShowWindow(fixture.Handle,5); SetForegroundWindow(fixture.Handle); editor.Focus(); Wait(()=>GetForegroundWindow()==fixture.Handle,600);
                        if(GetForegroundWindow()!=fixture.Handle || InputReader.FocusWindow()!=editor.Handle) lines.Add("BLOCKED speech focus fixture cannot receive input focus");
                        else {
                            panel.ShowNear(source); Application.DoEvents();
                            SendMessage(read.Handle,0x201,new IntPtr(1),new IntPtr(0x000a000a)); SendMessage(read.Handle,0x202,IntPtr.Zero,new IntPtr(0x000a000a));
                            check(GetForegroundWindow()==fixture.Handle && InputReader.FocusWindow()==editor.Handle && editor.Text=="输入保持不变" && player.IsSpeaking,"clicking pronunciation preserves the target editor and its text");
                            check(IsWindowVisible(read.Handle),"pronunciation button is visible in the displayed card");
                            using(Bitmap preview=new Bitmap(panel.Width,panel.Height)) { panel.DrawToBitmap(preview,new Rectangle(Point.Empty,panel.Size)); preview.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"speech-preview.png")); }
                            check(All(panel).OfType<Button>().All(b=>b.Width>=55 && b.Bounds.Right<=b.Parent.ClientSize.Width),"five actions fit the pronunciation card at the current DPI");
                        }
                        panel.Hide(); fixture.Close();
                    }
                }
                using(MemoryStream audio=new MemoryStream()) using(EnglishSpeech speech=new EnglishSpeech(audio)) {
                    string error=speech.Play("Hello. This is the HoverLex pronunciation test.");
                    check(error.Length==0,"installed Windows English voice accepts sentence synthesis: "+error);
                    Wait(()=>!speech.IsSpeaking,8000);
                    check(!speech.IsSpeaking && speech.Error.Length==0 && audio.Length>44 && audio.ToArray().Skip(44).Any(b=>b!=0),"real English speech produces nonempty WAV audio and completes");
                    speech.Play("This is a longer sentence used to check that playback can be interrupted."); speech.Stop();
                    check(!speech.IsSpeaking,"real speech cancellation clears active playback");
                }
                using(EnglishSpeech device=new EnglishSpeech()) {
                    string error=device.Play("Hello."); check(error.Length==0,"default audio output accepts English playback: "+error);
                    Wait(()=>!device.IsSpeaking,6000); check(!device.IsSpeaking && device.Error.Length==0,"default audio playback completes without engine errors");
                }
            } catch(Exception error) { lines.Add("FAIL "+error); }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"speech-tests.txt"),lines,Encoding.UTF8);
            return lines.Any(l=>l.StartsWith("FAIL")) ? 1 : lines.Any(l=>l.StartsWith("BLOCKED")) ? 3 : 0;
        }
    }
}
