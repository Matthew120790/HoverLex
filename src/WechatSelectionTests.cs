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
    internal static class WechatSelectionTests
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
        public static int Prepared()
        {
            const string expected="Curiosity makes learning easier.";
            List<string> lines=new List<string>(); int exit=0;
            Action<bool,string> check=(ok,name)=> { lines.Add((ok ? "PASS " : "FAIL ")+name); if(!ok) exit=1; };
            string report=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"wechat-prepared-tests.txt");
            try {
                Process process=Process.GetProcesses().FirstOrDefault(candidate=>(candidate.ProcessName=="Weixin" || candidate.ProcessName=="WeChat") && candidate.MainWindowHandle!=IntPtr.Zero);
                if(process==null) { lines.Add("BLOCKED WeChat main window not found"); return 3; }
                IntPtr window=process.MainWindowHandle; process.Dispose();
                ShowWindow(window,4); SetForegroundWindow(window); Application.DoEvents(); Thread.Sleep(200);
                IntPtr focus=InputReader.SelectionFocusWindow();
                lines.Add("INFO "+InputReader.WechatMetadata());
                if(!InputReader.SelectionWechat(focus)) { lines.Add("BLOCKED WeChat test selection is not focused; no key sent"); return 3; }
                var bounds=System.Windows.Automation.AutomationElement.FromHandle(window).Current.BoundingRectangle;
                Point point=new Point((int)(bounds.X+bounds.Width*.6),(int)(bounds.Bottom-180));
                bool hadText=Clipboard.ContainsText(); string previous=hadText ? Clipboard.GetText() : "";
                InputReader.SelectionReadTrace=message=>lines.Add("TRACE "+message);
                InputSnapshot source=InputReader.CopyCompatibilitySelection(focus.ToInt64(),point.X,point.Y);
                if(source.Text.Trim()!=expected) { lines.Add("BLOCKED WeChat selection differs from the agreed sentence; length="+source.Text.Length+" error="+source.Error); return 3; }
                check(true,"real WeChat reads only the prepared English selection");
                check(Clipboard.ContainsText()==hadText && (!hadText || Clipboard.GetText()==previous),"real WeChat selected-text read restores clipboard text");
                string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HoverLex","UserData");
                Settings settings=Settings.Load(Path.Combine(directory,"settings.json"));
                TranslationPreferences.Configure(settings.TranslationProvider,settings.DeepSeekModel,ApiKeyStore.Load(directory));
                OnlineTranslator service=new OnlineTranslator { CorrectionEnabled=settings.EnglishCorrection };
                string translated=service.TranslateAsync(source.Text,TranslationDirection.EnglishToChinese,CancellationToken.None).GetAwaiter().GetResult();
                check(InputTranslationGate.HasChinese(translated),"real WeChat selection translates into Chinese using the configured provider");
                using(var panel=new TranslationPanel()) {
                    panel.PendingSelection(source); panel.SelectionResult(source,translated); panel.ShowNear(source); Application.DoEvents();
                    check(panel.IsShown && panel.English==translated && !panel.CanReplace,"WeChat selection result card displays translation without replacing the draft");
                    panel.Hide();
                }
                InputSnapshot after=InputReader.CopyCompatibilitySelection(focus.ToInt64(),point.X,point.Y);
                check(after.Text.Trim()==expected,"real WeChat selection is unchanged after translation");
                check(Clipboard.ContainsText()==hadText && (!hadText || Clipboard.GetText()==previous),"real WeChat translation leaves clipboard text unchanged");
                service.ClearCache();
            } catch(Exception error) { check(false,error.ToString()); }
            finally { InputReader.SelectionReadTrace=null; File.WriteAllLines(report,lines); }
            return exit;
        }
        // 实机验证只监听拖选和 Ctrl，不输入文字、不发送消息。
        public static int Run(string expected,bool online)
        {
            List<string> lines=new List<string>(); int exit=3,calls=0; bool completed=false;
            string report=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"wechat-selection-tests.txt");
            bool hadText=Clipboard.ContainsText(); string oldText=hadText ? Clipboard.GetText() : "";
            string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HoverLex","UserData");
            Settings settings=Settings.Load(Path.Combine(directory,"settings.json"));
            TranslationPreferences.Configure(settings.TranslationProvider,settings.DeepSeekModel,ApiKeyStore.Load(directory));
            OnlineTranslator service=new OnlineTranslator { CorrectionEnabled=false };
            using(ApplicationContext context=new ApplicationContext())
            using(SelectionTranslationController controller=new SelectionTranslationController(message=> {if(message=="英文句子已翻译 · 可复制译文") completed=true; lines.Add("INFO "+message); File.WriteAllLines(report,lines);},async (text,direction,token)=> {
                calls++;
                if(text.Trim()!=expected.Trim() || !InputReader.SelectionWechat(InputReader.SelectionFocusWindow())) throw new InvalidOperationException("实机选区与测试短句不一致");
                lines.Add("PASS real WeChat reader captures only the mouse-selected phrase");
                return online ? await service.TranslateAsync(text,direction,token) : "我可以帮忙。";
            }))
            using(System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer { Interval=100 }) {
                DateTime end=DateTime.UtcNow.AddMinutes(4);
                controller.EnglishCorrection=false; controller.Enabled=true;
                controller.Trace=message=> { lines.Add("TRACE "+message); File.WriteAllLines(report,lines); };
                lines.Add("READY select the agreed English phrase in WeChat, then tap Ctrl"); File.WriteAllLines(report,lines);
                timer.Tick+=delegate {
                    if(completed && calls>0 && controller.Panel.IsShown && System.Text.RegularExpressions.Regex.IsMatch(controller.Panel.English,"[\\u3400-\\u9fff]")) {
                        timer.Stop();
                        using(var image=new System.Drawing.Bitmap(controller.Panel.Width,controller.Panel.Height)) {controller.Panel.DrawToBitmap(image,new System.Drawing.Rectangle(System.Drawing.Point.Empty,image.Size)); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"wechat-selection-preview.png"));}
                        lines.Add("PASS real WeChat mouse selection plus Ctrl displays Chinese translation");
                        bool restored=Clipboard.ContainsText()==hadText && (!hadText || Clipboard.GetText()==oldText);
                        lines.Add((restored ? "PASS " : "FAIL ")+"real WeChat compatibility reader preserves the original clipboard text");
                        var snapshot=InputReader.CopyCompatibilitySelection(InputReader.SelectionFocusWindow().ToInt64(),Cursor.Position.X,Cursor.Position.Y);
                        bool same=snapshot.Text.Trim()==expected.Trim();
                        lines.Add((same ? "PASS " : "FAIL ")+"real WeChat selection remains unchanged after translation");
                        exit=restored && same && calls==1 ? 0 : 1; timer.Stop(); File.WriteAllLines(report,lines); context.ExitThread();
                    } else if(DateTime.UtcNow>=end) {lines.Add("BLOCKED no completed WeChat selection translation within the test window"); timer.Stop(); File.WriteAllLines(report,lines); context.ExitThread();}
                };
                timer.Start(); Application.Run(context);
            }
            service.ClearCache(); return exit;
        }
    }
}
