using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverLex
{
    internal static class SelectionTests
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll",EntryPoint="SendMessageW")] private static extern IntPtr SendMessage(IntPtr window,int message,IntPtr value,IntPtr data);
        private const string Sentence="Curiosity makes learning easier.";
        private const string ChineseSentence="好奇心让学习更轻松。";
        private sealed class Voice : IEnglishSpeech
        {
            public event Action Changed;
            public bool IsSpeaking { get { return false; } }
            public string Error { get { return ""; } }
            public string Text="";
            public string Play(string text) { Text=text; if(Changed!=null) Changed(); return ""; }
            public void Stop() { }
            public void Dispose() { Changed=null; }
        }
        private static IEnumerable<Control> All(Control root) { foreach(Control child in root.Controls) { yield return child; foreach(Control nested in All(child)) yield return nested; } }
        private static async Task<bool> Wait(Func<bool> condition,int timeout=5000)
        {
            Stopwatch clock=Stopwatch.StartNew(); while(clock.ElapsedMilliseconds<timeout) { if(condition()) return true; await Task.Delay(40); } return condition();
        }
        private static async Task Focus(Form form,Control control)
        {
            for(int attempt=0;attempt<10;attempt++) {
                RestoreFixture(form.Handle);
                form.Activate(); SetForegroundWindow(form.Handle); form.ActiveControl=control; control.Select(); control.Focus(); await Task.Delay(100);
                if(GetForegroundWindow()==form.Handle && InputReader.FocusWindow()==control.Handle) return;
            }
            throw new InvalidOperationException("own selection fixture lost keyboard focus; no key sent; expected="+control.Handle+" actual="+InputReader.FocusWindow()+" foreground="+GetForegroundWindow()+" fixture="+form.Handle+" kind="+control.GetType().Name+" visible="+control.Visible+" enabled="+control.Enabled+" canFocus="+control.CanFocus);
        }
        [DllImport("user32.dll",EntryPoint="ShowWindow")] private static extern bool ShowFixture(IntPtr window,int command);
        private static void RestoreFixture(IntPtr window) { ShowFixture(window,9); ShowFixture(window,5); }
        public static int Run(bool online)
        {
            List<string> lines=new List<string>(); int exit=0;
            Action<bool,string> check=(pass,name)=> { lines.Add((pass ? "PASS " : "FAIL ")+name); if(!pass) exit=1; };
            SelectionShortcut gate=new SelectionShortcut();
            gate.Key(0xa2,true); gate.Key(0xa2,true); gate.Key(0xa2,false);
            check(gate.Candidate && gate.Released && gate.Gesture==1,"quick Ctrl tap and repeats produce one gesture");
            gate.Key(0xa2,true); gate.Key(0x43,true); gate.Key(0xa2,false);
            check(!gate.Candidate && !gate.Released,"Ctrl+C cancels selection translation");
            gate.Key(0xa3,true,true); gate.Key(0xa3,false);
            check(!gate.Candidate,"Ctrl with Shift/Alt/Win does not translate");
            gate.Key(0xa2,true); gate.Key(0xa3,true); gate.Key(0xa2,false);
            check(!gate.Released,"two Ctrl keys wait for both releases"); gate.Key(0xa3,false);
            check(gate.Released,"right Ctrl also completes the gesture");
            SelectionIntent intent=new SelectionIntent(); intent.Selected(7); intent.Key(0x43,true,false,false,7);
            check(intent.Matches(7) && !intent.Matches(8),"copying preserves a selected region only in the same window");
            intent.Key(0x41,true,false,false,7); check(intent.Matches(7),"Ctrl+A records an explicit keyboard selection");
            intent.Clear(); intent.Key(0x27,false,true,false,7); check(intent.Matches(7),"Shift+arrow records an explicit keyboard selection");
            intent.Key(0x56,true,false,false,7); check(!intent.Matches(7),"pasting invalidates the previous selection");
            intent.Selected(7); intent.Key(0x27,false,false,false,7); check(!intent.Matches(7),"moving the caret invalidates the previous selection");
            intent.Selected(7); intent.Key(0x42,false,false,false,7); check(!intent.Matches(7),"typing invalidates the previous selection");
            check(SelectionTranslationController.Eligible(Sentence) && SelectionTranslationController.Eligible(ChineseSentence) && SelectionTranslationController.Eligible("你好 world") && SelectionTranslationController.Eligible("歡迎學習英文。"),"English, Chinese, mixed and traditional Chinese selections translate");
            check(!SelectionTranslationController.Eligible(null) && !SelectionTranslationController.Eligible(" \r\n") && !SelectionTranslationController.Eligible("1234 !?") && !SelectionTranslationController.Eligible(new string('a',2001)) && !SelectionTranslationController.Eligible(new string('中',2001)) && SelectionTranslationController.Eligible(new string('中',2000)),"empty and non-language selections are excluded and the 2000-character limit applies to both languages");
            using(Form fixture=new Form { Text="HoverLex owned selection fixture",Size=new Size(760,420),TopMost=true,StartPosition=FormStartPosition.CenterScreen })
            using(TextBox editor=new TextBox { Multiline=true,ReadOnly=true,Text="First line.\r\n"+Sentence+"\r\nLast line.",Location=new Point(20,20),Size=new Size(700,90) })
            using(RichTextBox rich=new RichTextBox { ReadOnly=true,Text="First line.\n"+Sentence+"\nLast line.",Location=new Point(20,125),Size=new Size(700,90) })
            using(TextBox secret=new TextBox { UseSystemPasswordChar=true,Text=Sentence,Location=new Point(20,230),Width=500 }) {
                fixture.Controls.AddRange(new Control[] { editor,rich,secret });
                fixture.Shown+=async delegate {
                    try {
                        await Focus(fixture,editor); editor.Select(editor.Text.IndexOf(Sentence),Sentence.Length);
                        Point point=editor.PointToScreen(new Point(30,25)); uint clipboard=GetClipboardSequenceNumber();
                        check(InputReader.SelectionClipboardOwnerMatches(editor.Handle,(uint)Process.GetCurrentProcess().Id),"clipboard owner from the exact source process is accepted");
                        check(!InputReader.SelectionClipboardOwnerMatches(editor.Handle,0) && !InputReader.SelectionClipboardOwnerMatches(editor.Handle,UInt32.MaxValue),"missing or unrelated clipboard owners are rejected");
                        InputSnapshot selected=await SelectionTranslationController.ReadAsync(editor.Handle.ToInt64(),point,CancellationToken.None);
                        check(selected.Text==Sentence && !selected.Editable,"isolated reader gets exact readonly selection without changing the text");
                        check(editor.SelectedText==Sentence && GetClipboardSequenceNumber()==clipboard,"reading preserves selection and clipboard");
                        bool hadText=Clipboard.ContainsText(); string oldText=hadText ? Clipboard.GetText() : "";
                        InputSnapshot copied=InputReader.CopySelectionFixture(editor.Handle.ToInt64(),point.X,point.Y);
                        check(copied.Text==Sentence && editor.SelectedText==Sentence,"compatibility copy reads only the selected sentence without changing selection");
                        check(Clipboard.ContainsText()==hadText && (!hadText || Clipboard.GetText()==oldText),"compatibility copy restores original clipboard text");
                        clipboard=GetClipboardSequenceNumber(); copied=InputReader.CopyCompatibilitySelection(editor.Handle.ToInt64(),point.X,point.Y);
                        check(copied.Text.Length==0 && GetClipboardSequenceNumber()==clipboard,"unsupported apps never receive compatibility copy");
                        editor.Select(0,0); selected=await SelectionTranslationController.ReadAsync(editor.Handle.ToInt64(),point,CancellationToken.None);
                        check(selected.Text.Length==0,"collapsed caret does not translate the whole document");
                        clipboard=GetClipboardSequenceNumber(); copied=InputReader.CopySelectionFixture(editor.Handle.ToInt64(),point.X,point.Y);
                        check(copied.Text.Length==0 && GetClipboardSequenceNumber()==clipboard,"compatibility copy with no selection never returns old clipboard text");
                        await Focus(fixture,rich); rich.Select(rich.Text.IndexOf(Sentence),Sentence.Length);
                        point=rich.PointToScreen(new Point(30,25)); selected=await SelectionTranslationController.ReadAsync(rich.Handle.ToInt64(),point,CancellationToken.None);
                        check(selected.Text==Sentence,"readonly rich text selection after a line break has correct offsets: "+selected.Text);
                        await Focus(fixture,secret); secret.SelectAll(); selected=await SelectionTranslationController.ReadAsync(secret.Handle.ToInt64(),point,CancellationToken.None);
                        check(selected.Text.Length==0 && selected.Error.Contains("密码"),"password selection is never read");
                        clipboard=GetClipboardSequenceNumber(); copied=InputReader.CopySelectionFixture(secret.Handle.ToInt64(),secret.PointToScreen(new Point(20,10)).X,secret.PointToScreen(new Point(20,10)).Y);
                        check(copied.Text.Length==0 && copied.Error.Contains("密码") && GetClipboardSequenceNumber()==clipboard,"compatibility copy excludes password fields before sending keys");
                        await Focus(fixture,editor); selected=await SelectionTranslationController.ReadAsync(secret.Handle.ToInt64(),point,CancellationToken.None);
                        check(selected.Text.Length==0,"changed focus discards the selection");
                        int calls=0; string received="",status=""; TranslationDirection lastDirection=TranslationDirection.ChineseToEnglish;
                        using(SelectionTranslationController controller=new SelectionTranslationController(message=> { status=message; lines.Add("INFO "+message); },async (text,direction,token)=> { calls++; received=text; lastDirection=direction; await Task.Delay(300,token); return direction==TranslationDirection.EnglishToChinese ? ChineseSentence : Sentence; })) {
                            controller.Trace=message=>lines.Add("TRACE "+message);
                            controller.Enabled=true; controller.EnglishCorrection=false;
                            await Focus(fixture,editor); editor.Select(0,0);
                            TranslationTests.KeyOwnedWindow(fixture.Handle,editor.Handle,0x11); await Task.Delay(700);
                            check(calls==0 && !controller.Panel.IsShown && !controller.SuppressHover,"no selection leaves Ctrl hover lookup available");
                            await Focus(fixture,editor); editor.Select(editor.Text.IndexOf(Sentence),Sentence.Length);
                            TranslationTests.KeyOwnedWindow(fixture.Handle,editor.Handle,0x11);
                            bool result=await Wait(()=>controller.Panel.English=="好奇心让学习更轻松。" && controller.Panel.IsShown);
                            check(result && calls==1 && received==Sentence && lastDirection==TranslationDirection.EnglishToChinese && All(controller.Panel).Any(c=>c.Text=="选中英文 → 中文"),"single quick Ctrl tap routes English to Chinese and shows the correct title");
                            check(!controller.Panel.CanReplace && editor.SelectedText==Sentence && InputReader.FocusWindow()==editor.Handle,"selection popup never replaces text or steals focus");
                            using(Bitmap bitmap=new Bitmap(controller.Panel.Width,controller.Panel.Height)) { controller.Panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selection-preview.png")); }
                            TranslationTests.KeyOwnedWindow(fixture.Handle,editor.Handle,0x1b); await Task.Delay(150);
                            check(!controller.Panel.IsShown,"Esc dismisses selection translation");
                            await Focus(fixture,editor); editor.Select(editor.Text.IndexOf(Sentence),Sentence.Length);
                            TranslationTests.KeyOwnedWindow(fixture.Handle,editor.Handle,0x11);
                            await Wait(()=>calls==2);
                            editor.Select(0,5);
                            await Task.Delay(900);
                            check(!controller.Panel.IsShown,"programmatic selection change discards a pending translation");
                            await Focus(fixture,editor); editor.Select(editor.Text.IndexOf(Sentence),Sentence.Length);
                            TranslationTests.KeyOwnedWindow(fixture.Handle,editor.Handle,0x11);
                            await Wait(()=>calls==3);
                            controller.EnglishCorrection=true;
                            await Task.Delay(500);
                            check(controller.EnglishCorrection && !controller.Panel.IsShown,"changing English correction cancels the old selection result");
                            controller.EnglishCorrection=false;
                            await Focus(fixture,editor); editor.Select(editor.Text.IndexOf(Sentence),Sentence.Length);
                            TranslationTests.KeyOwnedWindow(fixture.Handle,editor.Handle,0x11);
                            await Wait(()=>calls==4);
                            await Focus(fixture,rich); await Task.Delay(600);
                            check(!controller.Panel.IsShown,"switching to another control cancels selection translation");
                            controller.Enabled=false;
                            await Focus(fixture,editor); editor.Select(editor.Text.IndexOf(Sentence),Sentence.Length);
                            TranslationTests.KeyOwnedWindow(fixture.Handle,editor.Handle,0x11); await Task.Delay(500);
                            check(calls==4 && !controller.Panel.IsShown,"pause disables selection translation");
                            controller.Enabled=true;
                            editor.Text="第一行。\r\n"+ChineseSentence+"\r\n最后一行。";
                            await Focus(fixture,editor); editor.Select(editor.Text.IndexOf(ChineseSentence),ChineseSentence.Length);
                            TranslationTests.KeyOwnedWindow(fixture.Handle,editor.Handle,0xa3);
                            result=await Wait(()=>controller.Panel.English==Sentence && controller.Panel.IsShown);
                            check(result && calls==5 && received==ChineseSentence && lastDirection==TranslationDirection.ChineseToEnglish && status=="中文句子已翻译 · 可复制译文","right Ctrl translates exactly the selected Chinese sentence into English with correction off");
                            check(All(controller.Panel).Any(c=>c.Text=="选中中文 → 英文") && All(controller.Panel).Any(c=>c.AccessibleName=="英文译文") && !controller.Panel.CanReplace && editor.SelectedText==ChineseSentence && InputReader.FocusWindow()==editor.Handle,"Chinese selection popup has correct direction and preserves text, selection and focus");
                            using(Bitmap bitmap=new Bitmap(controller.Panel.Width,controller.Panel.Height)) { controller.Panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selection-chinese-preview.png")); }
                            controller.Panel.Retry(); await Wait(()=>calls==6);
                            editor.Select(0,3); await Task.Delay(900);
                            check(!controller.Panel.IsShown && lastDirection==TranslationDirection.ChineseToEnglish,"changing a Chinese selection discards a pending retry");
                            editor.Text="明天讨论 HoverLex 的更新。";
                            await Focus(fixture,editor); editor.SelectAll();
                            TranslationTests.KeyOwnedWindow(fixture.Handle,editor.Handle,0x11);
                            result=await Wait(()=>controller.Panel.English==Sentence && controller.Panel.IsShown);
                            check(result && calls==7 && received==editor.Text && lastDirection==TranslationDirection.ChineseToEnglish,"mixed Chinese and English selection translates into English: calls="+calls+" received="+received+" status="+status);
                        }
                        using(Voice voice=new Voice()) using(TranslationPanel popup=new TranslationPanel(voice)) {
                            InputSnapshot englishSource=new InputSnapshot { Text=Sentence },chineseSource=new InputSnapshot { Text=ChineseSentence };
                            Button read=(Button)popup.Controls.Find("speakTranslation",true)[0];
                            popup.SelectionResult(englishSource,ChineseSentence); popup.ShowNear(englishSource);
                            SendMessage(read.Handle,0x201,new IntPtr(1),new IntPtr(0x000a000a)); SendMessage(read.Handle,0x202,IntPtr.Zero,new IntPtr(0x000a000a));
                            check(voice.Text==Sentence && read.AccessibleName=="朗读英文原文","English selection pronunciation reads the original English sentence");
                            popup.PendingSelection(chineseSource);
                            check(!read.Enabled && All(popup).Any(c=>c.Text=="选中中文 → 英文"),"Chinese pending popup shows the direction before the result arrives");
                            popup.SelectionResult(chineseSource,Sentence);
                            bool chineseNote=All(popup).Any(c=>c.Text=="复制英文译文 · 朗读英文译文");
                            SendMessage(read.Handle,0x201,new IntPtr(1),new IntPtr(0x000a000a)); SendMessage(read.Handle,0x202,IntPtr.Zero,new IntPtr(0x000a000a));
                            check(voice.Text==Sentence && read.AccessibleName=="朗读英文译文" && chineseNote,"Chinese selection pronunciation reads the English translation");
                        }
                        if(online) {
                            OnlineTranslator service=new OnlineTranslator(); service.CorrectionEnabled=false;
                            string result=await service.TranslateAsync("Hello, world.",TranslationDirection.EnglishToChinese,CancellationToken.None);
                            check(System.Text.RegularExpressions.Regex.IsMatch(result,"[\\u3400-\\u9fff]"),"actual selected English to Chinese online service: "+result);
                            result=await service.TranslateAsync(ChineseSentence,OnlineTranslator.DirectionFor(ChineseSentence),CancellationToken.None);
                            check(!String.IsNullOrWhiteSpace(result) && !InputTranslationGate.HasChinese(result),"actual selected Chinese to English online service: "+result);
                        }
                    } catch(InvalidOperationException error) { if(error.Message.Contains("focus")) { lines.Add("BLOCKED "+error.Message); if(exit==0) exit=3; } else { lines.Add("FAIL "+error); exit=1; } }
                    catch(Exception error) { lines.Add("FAIL "+error); exit=1; }
                    finally { fixture.Close(); }
                };
                Application.Run(fixture);
            }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"selection-tests.txt"),lines);
            return exit;
        }
    }
}
