using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace HoverLex
{
    internal static class WhatsAppTests
    {
        private const string Sentence="Curiosity makes learning easier.";
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        internal static int Readonly(long handle)
        {
            var lines=new List<string>(); int failures=0;
            Action<bool,string> check=(ok,name)=> { lines.Add((ok ? "PASS " : "FAIL ")+name); if(!ok) failures++; };
            try {
                IntPtr host=WhatsAppWindows.Host(new IntPtr(handle));
                if(host==IntPtr.Zero) return Save(lines,3);
                ShowWindow(host,9); SetForegroundWindow(host); Thread.Sleep(300);
                AutomationElement editor=null; string contents=""; TextPattern pattern=null;
                foreach(var renderer in WhatsAppWindows.Renderers(host)) {
                    var nodes=AutomationElement.FromHandle(renderer).FindAll(TreeScope.Subtree,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit));
                    foreach(AutomationElement node in nodes) {
                        object value,text;
                        if(node.Current.IsPassword || node.Current.IsOffscreen || !node.TryGetCurrentPattern(ValuePattern.Pattern,out value) || ((ValuePattern)value).Current.IsReadOnly) continue;
                        string input=((ValuePattern)value).Current.Value;
                        if(String.IsNullOrEmpty(input) || input.Length>2000 || !node.TryGetCurrentPattern(TextPattern.Pattern,out text)) continue;
                        if(editor!=null) { lines.Add("BLOCKED multiple nonempty editors; no focus changed"); return Save(lines,3); }
                        editor=node; contents=input; pattern=(TextPattern)text;
                    }
                }
                if(editor==null) { lines.Add("BLOCKED no nonempty composer; no text changed"); return Save(lines,3); }
                editor.SetFocus(); Thread.Sleep(200);
                InputSnapshot snapshot=InputReader.ReadFocused();
                check(snapshot.Editable && snapshot.Mode=="whatsapp-value" && snapshot.Text==contents,"actual WhatsApp snapshot equals only the editor value");
                check(InputTranslationGate.HasChinese(snapshot.Text)==InputTranslationGate.HasChinese(contents),"surrounding Chinese UI cannot change English input into Chinese translation");
                int tested=0;
                foreach(Match match in Regex.Matches(contents,@"[A-Za-z]+(?:[-'][A-Za-z]+)*")) {
                    if(tested>=3 || match.Length<3) continue;
                    var range=pattern.DocumentRange.FindText(match.Value,false,false); if(range==null) continue;
                    var bounds=range.GetBoundingRectangles(); if(bounds.Length==0) continue;
                    foreach(double part in new[] { .2,.5,.8 }) {
                        var r=bounds[0]; var timer=Stopwatch.StartNew();
                        var captured=ScreenReader.AutomationOnly((int)(r.X+r.Width*part),(int)(r.Y+r.Height*.5));
                        check(String.Equals(captured.Word,match.Value,StringComparison.OrdinalIgnoreCase),"actual WhatsApp word "+tested+" glyph point "+part+" captures the exact word ("+timer.ElapsedMilliseconds+" ms)");
                    }
                    tested++;
                }
                check(tested>0,"visible English word glyphs were tested");
                object after;
                check(editor.TryGetCurrentPattern(ValuePattern.Pattern,out after) && ((ValuePattern)after).Current.Value==contents,"readonly checks preserve the complete original draft");
            } catch(Exception error) { lines.Add("FAIL "+error.GetType().Name+": "+error.Message); failures++; }
            return Save(lines,failures==0 ? 0 : 1);
        }
        internal static int Run(bool prepared,bool online)
        {
            var lines=new List<string>(); int failures=0;
            Action<bool,string> check=(ok,name)=> { lines.Add((ok ? "PASS " : "FAIL ")+name); if(!ok) failures++; };
            try {
                IntPtr host=WhatsAppWindows.Host(GetForegroundWindow());
                if(host==IntPtr.Zero) { lines.Add("BLOCKED foreground is not WhatsApp"); return Save(lines,3); }
                var renderers=WhatsAppWindows.Renderers(host);
                check(renderers.Count>0,"actual WhatsApp exposes separately hosted renderer");
                check(InputReader.FocusWindow()==host && InputReader.SelectionFocusWindow()==host,"native and selection focus consistently use WhatsApp host");
                check(WhatsAppWindows.Host(Process.GetCurrentProcess().MainWindowHandle)==IntPtr.Zero,"unrelated app is excluded");
                foreach(var renderer in renderers) check(InputReader.SelectionWindowMatches(renderer,host.ToInt64()),"renderer belongs to exact WhatsApp host");
                var timer=Stopwatch.StartNew(); InputSnapshot source=InputReader.ReadFocused();
                lines.Add("INFO focused input editable="+source.Editable+" mode="+source.Mode+" length="+source.Text.Length+" preparedSentencePresent="+source.Text.Contains(Sentence)+" readMilliseconds="+timer.ElapsedMilliseconds);
                if(!prepared) return Save(lines,failures==0 ? 0 : 1);
                AutomationElement editor=WhatsAppWindows.FocusedEditor(host);
                if(editor==null || !source.Text.Contains(Sentence)) { lines.Add("BLOCKED focus the unsent prepared English sentence before testing"); return Save(lines,3); }
                check(source.Editable && source.Mode=="whatsapp-value" && source.Error.Length==0,"prepared WhatsApp composer reads only its own value");
                object actualValue;
                check(editor.TryGetCurrentPattern(ValuePattern.Pattern,out actualValue) && ((ValuePattern)actualValue).Current.Value==source.Text,"snapshot equals the actual composer value and excludes surrounding UI text");
                object value; if(!editor.TryGetCurrentPattern(TextPattern.Pattern,out value)) throw new InvalidOperationException("prepared editor has no text pattern");
                TextPattern pattern=(TextPattern)value; var original=pattern.GetSelection(); string draft=pattern.DocumentRange.GetText(2001);
                var word=pattern.DocumentRange.FindText("Curiosity",false,false); var bounds=word==null ? new System.Windows.Rect[0] : word.GetBoundingRectangles();
                check(bounds.Length>0,"prepared English word has exact glyph bounds");
                if(bounds.Length>0) {
                    Point point=new Point((int)(bounds[0].X+bounds[0].Width/2),(int)(bounds[0].Y+bounds[0].Height/2));
                    var captured=ScreenReader.AutomationOnly(point.X,point.Y);
                    lines.Add("INFO hover method="+captured.Method+" error="+captured.Error+" matchesPreparedWord="+String.Equals(captured.Word,"Curiosity",StringComparison.OrdinalIgnoreCase));
                    check(String.Equals(captured.Word,"Curiosity",StringComparison.OrdinalIgnoreCase),"actual WhatsApp hover captures Curiosity without screenshot");
                    pattern.DocumentRange.FindText(Sentence,false,false).Select();
                    try {
                        var selected=InputReader.ReadSelection(host.ToInt64(),point.X,point.Y);
                        check(selected.Text.Trim()==Sentence,"actual WhatsApp selection reads prepared sentence");
                    } finally { if(InputReader.FocusWindow()==host && pattern.DocumentRange.GetText(2001)==draft && original.Length==1) original[0].Select(); }
                }
                check(pattern.DocumentRange.GetText(2001)==draft,"draft is unchanged and no message is sent");
                check(InputReader.SameSource(source,InputReader.ReadFocused()),"composer source and caret are stable after readonly probes");
                if(online) {
                    string data=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HoverLex","UserData");
                    Settings settings=Settings.Load(Path.Combine(data,"settings.json"));
                    TranslationPreferences.Configure(settings.TranslationProvider,settings.DeepSeekModel,ApiKeyStore.Load(data));
                    var translator=new OnlineTranslator();
                    var translated=translator.TranslateAsync(Sentence,TranslationDirection.EnglishToChinese,CancellationToken.None).GetAwaiter().GetResult();
                    check(InputTranslationGate.HasChinese(translated),"configured provider translates the WhatsApp selected English fixture into Chinese");
                    translated=translator.TranslateAsync("好奇心让学习更轻松。",TranslationDirection.ChineseToEnglish,CancellationToken.None).GetAwaiter().GetResult();
                    check(!InputTranslationGate.HasChinese(translated) && InputTranslationGate.HasInput(translated),"configured provider translates the Chinese composer fixture into English");
                    translated=translator.TranslateAsync("I has an apple.",TranslationDirection.ChineseToEnglish,CancellationToken.None).GetAwaiter().GetResult();
                    check(translated.IndexOf("have",StringComparison.OrdinalIgnoreCase)>=0 && translated.IndexOf(" has ",StringComparison.OrdinalIgnoreCase)<0,"configured provider corrects the English composer fixture");
                }
            } catch(Exception error) { lines.Add("FAIL "+error.GetType().Name+": "+error.Message); failures++; }
            return Save(lines,failures==0 ? 0 : 1);
        }
        private static int Save(List<string> lines,int code) { File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"whatsapp-tests.txt"),lines.ToArray()); return code; }
    }
}
