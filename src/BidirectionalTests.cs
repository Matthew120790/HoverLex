using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverLex
{
    public static class BidirectionalTests
    {
        private sealed class Fixture : Form
        {
            internal SearchTranslation Search;
            protected override bool ProcessCmdKey(ref Message message,Keys keyData) { if(keyData==Keys.Tab && Search!=null && Search.HandleTab()) return true; return base.ProcessCmdKey(ref message,keyData); }
        }
        private sealed class Voice : IEnglishSpeech
        {
            public event Action Changed;
            public bool IsSpeaking { get; private set; }
            public string Error { get { return ""; } }
            public string Text="";
            public string Play(string text) { Text=text; IsSpeaking=true; if(Changed!=null) Changed(); return ""; }
            public void Stop() { IsSpeaking=false; if(Changed!=null) Changed(); }
            public void Dispose() { Changed=null; }
        }
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr value,IntPtr data);
        private static IEnumerable<Control> All(Control root) { foreach(Control child in root.Controls) { yield return child; foreach(Control nested in All(child)) yield return nested; } }
        public static int Run(bool online)
        {
            List<string> lines=new List<string>(); int exit=1; Action<bool,string> check=(ok,text)=>lines.Add((ok ? "PASS " : "FAIL ")+text);
            using(Fixture fixture=new Fixture { Text="HoverLex bidirectional tests",ClientSize=new Size(720,300) })
            using(TextBox editor=new TextBox { Bounds=new Rectangle(20,20,640,40) }) using(Voice voice=new Voice()) {
                fixture.Controls.Add(editor); fixture.Shown+=async delegate {
                    try {
                        ShowWindow(fixture.Handle,5); SetForegroundWindow(fixture.Handle); editor.Focus(); await Task.Delay(150);
                        if(GetForegroundWindow()!=fixture.Handle || InputReader.FocusWindow()!=editor.Handle) { lines.Add("BLOCKED own bidirectional fixture lacks keyboard focus"); exit=3; return; }
                        int calls=0; string word=""; TranslationDirection last=TranslationDirection.ChineseToEnglish;
                        using(SearchTranslation search=new SearchTranslation(editor,message=> { },key=>key=="archive" ? new Entry { Word="archive",Chinese="存档" } : null,key=>word=key,(text,direction,cancel)=> { calls++; last=direction; return Task.FromResult(direction==TranslationDirection.EnglishToChinese ? "你好，世界。" : "Hello, world."); },voice)) {
                            fixture.Search=search;
                            editor.Text="Hello, world."; await search.QueryAsync();
                            check(last==TranslationDirection.EnglishToChinese && search.Panel.English=="你好，世界。" && All(search.Panel).Any(c=>c.Text=="英文 → 中文"),"main search translates English sentences into Chinese with the correct title");
                            Button read=(Button)search.Panel.Controls.Find("speakTranslation",true)[0];
                            SendMessage(read.Handle,0x201,new IntPtr(1),new IntPtr(0x000a000a)); SendMessage(read.Handle,0x202,IntPtr.Zero,new IntPtr(0x000a000a));
                            check(voice.Text=="Hello, world.","English-to-Chinese pronunciation reads the English source");
                            using(Bitmap preview=new Bitmap(search.Panel.Width,search.Panel.Height)) { search.Panel.DrawToBitmap(preview,new Rectangle(Point.Empty,search.Panel.Size)); preview.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"bidirectional-preview.png")); }
                            TranslationTests.KeyOwnedWindow(fixture.Handle,editor.Handle,9); await Task.Delay(150);
                            check(editor.Text=="你好，世界。" && InputReader.FocusWindow()==editor.Handle,"main search Tab replaces English with Chinese without moving focus");
                            await search.QueryAsync();
                            check(last==TranslationDirection.ChineseToEnglish && search.Panel.English=="Hello, world.","main search translates Chinese into English");
                            int before=calls; editor.Text="archive"; await search.QueryAsync();
                            check(word=="archive" && calls==before,"known English words retain offline dictionary definitions without online requests");
                            editor.Text="unknownwordfixture"; await search.QueryAsync(); check(calls==before+1 && last==TranslationDirection.EnglishToChinese,"unknown English words fall back to English-to-Chinese translation");
                            editor.Text="改变输入"; check(!search.Panel.IsShown && !search.HandleTab(),"editing the search field dismisses stale results and restores normal Tab");
                            fixture.Search=null;
                        }
                        TaskCompletionSource<string> pending=new TaskCompletionSource<string>();
                        using(SearchTranslation search=new SearchTranslation(editor,message=> { },null,null,(text,direction,cancel)=>pending.Task)) {
                            editor.Text="Old sentence."; Task request=search.QueryAsync(); editor.Text="New sentence."; pending.SetResult("旧译文"); await request;
                            check(editor.Text=="New sentence." && !search.Panel.IsShown,"late search translations cannot replace or display results for changed input");
                        }
                        check(OnlineTranslator.DirectionFor("Mixed 中文 text")==TranslationDirection.ChineseToEnglish,"mixed Chinese and English selects Chinese-to-English");
                        pending=new TaskCompletionSource<string>();
                        using(SearchTranslation search=new SearchTranslation(editor,message=> { },null,null,(text,direction,cancel)=>pending.Task)) {
                            editor.Text="I has an apple."; Task request=search.QueryAsync(); search.EnglishCorrection=false; pending.SetResult("我有一个苹果。"); await request;
                            check(editor.Text=="I has an apple." && !search.Panel.IsShown && !search.HandleTab(),"the correction switch cancels stale manual search results without replacing the source");
                        }
                        if(online) {
                            OnlineTranslator translator=new OnlineTranslator();
                            string chinese=await translator.TranslateAsync("Hello, we have a meeting tomorrow at 3pm.",TranslationDirection.EnglishToChinese,CancellationToken.None);
                            check(InputTranslationGate.HasChinese(chinese),"live service translates English sentences into Chinese");
                            string english=await translator.TranslateAsync("你好，明天下午三点开会。",TranslationDirection.ChineseToEnglish,CancellationToken.None);
                            check(!InputTranslationGate.HasChinese(english) && english.Length>0,"live service retains Chinese-to-English translation");
                            string repeat=await translator.TranslateAsync("Hello, we have a meeting tomorrow at 3pm.",TranslationDirection.EnglishToChinese,CancellationToken.None);
                            check(repeat==chinese,"English-to-Chinese results remain correct after using the opposite direction");
                        }
                        exit=lines.Any(l=>l.StartsWith("FAIL")) ? 1 : 0;
                    } catch(Exception error) { lines.Add("FAIL "+error); }
                    finally { File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"bidirectional-tests.txt"),lines,Encoding.UTF8); fixture.Close(); }
                }; Application.Run(fixture);
            }
            return exit;
        }
    }
}
