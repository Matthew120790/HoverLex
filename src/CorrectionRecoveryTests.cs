using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverLex
{
    internal static class CorrectionRecoveryTests
    {
        private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
        private static void Set(object target,string name,object value) { target.GetType().GetField(name,Fields).SetValue(target,value); }
        private static Button Retry(Control parent) {
            foreach(Control child in parent.Controls) { if(child is Button && child.Text=="重试") return (Button)child; Button found=Retry(child); if(found!=null) return found; }
            return null;
        }
        private static async Task Wait(Func<bool> ready) { for(int i=0;i<100 && !ready();i++) await Task.Delay(20); }
        // 注入快照，不抢焦点、不发送按键，也不读取用户输入。
        private static TranslationController Controller(Func<string,CancellationToken,Task<string>> translate,out InputMonitor monitor,out InputSnapshot source) {
            source=new InputSnapshot { Id="recovery-fixture",Text="I has an apple.",Editable=true,Mode="value",FocusHandle=InputReader.FocusWindow().ToInt64(),X=20,Y=20 };
            monitor=(InputMonitor)FormatterServices.GetUninitializedObject(typeof(InputMonitor)); Set(monitor,"latest",source);
            Fresh(monitor);
            var controller=new TranslationController(message=>{},translate);
            Set(controller,"activity",FormatterServices.GetUninitializedObject(typeof(InputActivity)));
            Set(controller,"monitor",monitor); Set(controller,"enabled",true);
            return controller;
        }
        private static void Fresh(InputMonitor monitor) { Set(monitor,"responded",true); Set(monitor,"received",Stopwatch.GetTimestamp()); }
        private static void Start(TranslationController controller,InputSnapshot source) {
            typeof(TranslationController).GetMethod("Translate",Fields).Invoke(controller,new object[] { source,0 });
        }
        public static int Run(bool online) {
            var lines=new List<string>(); Action<bool,string> check=(ok,name)=>lines.Add((ok ? "PASS " : "FAIL ")+name);
            using(var context=new ApplicationContext()) {
                EventHandler idle=null;
                idle=async (sender,args)=> {
                    Application.Idle-=idle;
                    try { await Scenarios(check,online); } catch(Exception error) { check(false,error.ToString()); }
                    finally { context.ExitThread(); }
                };
                Application.Idle+=idle;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Application.Run(context);
            }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"correction-recovery-tests.txt"),lines,Encoding.UTF8);
            return lines.Any(line=>line.StartsWith("FAIL")) ? 1 : 0;
        }
        private static async Task Scenarios(Action<bool,string> check,bool online) {
            InputMonitor monitor; InputSnapshot source; int attempts=0;
            using(var controller=Controller((text,cancel)=> {
                attempts++; if(attempts==1) throw new InvalidOperationException("Temporary checker failure");
                return Task.FromResult("I have an apple.");
            },out monitor,out source)) {
                Start(controller,source); await Wait(()=>controller.Panel.IsShown);
                Button retry=Retry(controller.Panel);
                check(controller.Panel.IsShown && controller.Panel.English=="Temporary checker failure" && !controller.Panel.CanReplace && retry!=null && retry.Enabled,"checker failure displays a visible English retry card");
                if(retry!=null) controller.Panel.Retry();
                await Wait(()=>controller.Panel.CanReplace);
                check(attempts==2 && controller.Panel.IsShown && controller.Panel.English=="I have an apple." && controller.Panel.CanReplace,"retry runs the actual controller and publishes a corrected English result");
                check(monitor.Latest.Text=="I has an apple.","failure and retry preserve the original input snapshot");
            }
            using(var controller=Controller((text,cancel)=>Task.FromResult("我有一个苹果。"),out monitor,out source)) {
                Start(controller,source); await Wait(()=>controller.Panel.IsShown);
                check(controller.Panel.IsShown && !controller.Panel.CanReplace && controller.Panel.English.Contains("结果无效") && Retry(controller.Panel).Enabled,"invalid Chinese output displays an error with replacement disabled");
            }
            using(var controller=Controller((text,cancel)=>Task.FromResult(text),out monitor,out source)) {
                Start(controller,source); await Task.Delay(80);
                check(!controller.Panel.IsShown,"an unchanged English result keeps the correction card hidden");
                check(controller.Feedback.IsShown && controller.Feedback.Message.Contains("检查完成"),"unchanged English gives a compact completion message instead of silent absence");
            }
            var delayed=new TaskCompletionSource<string>();
            using(var controller=Controller((text,cancel)=>delayed.Task,out monitor,out source)) {
                Start(controller,source); check(!controller.Panel.IsShown,"pending English correction stays hidden");
                Set(monitor,"latest",new InputSnapshot { Id=source.Id,Text=source.Text+" More text.",Editable=true,Mode=source.Mode,FocusHandle=source.FocusHandle });
                delayed.SetResult("I have an apple."); await Task.Delay(80);
                check(!controller.Panel.IsShown && !controller.Panel.CanReplace,"continued typing rejects a delayed correction in the actual controller");
            }
            delayed=new TaskCompletionSource<string>();
            using(var controller=Controller((text,cancel)=>delayed.Task,out monitor,out source)) {
                Start(controller,source); Set(monitor,"latest",null);
                delayed.SetException(new InvalidOperationException("Late failure")); await Task.Delay(80);
                check(!controller.Panel.IsShown,"provider loss suppresses an error for a stale source");
            }
            delayed=new TaskCompletionSource<string>();
            using(var controller=Controller((text,cancel)=>delayed.Task,out monitor,out source)) {
                Start(controller,source);
                var clock=(Stopwatch)typeof(TranslationController).GetField("clock",Fields).GetValue(controller);
                Set(controller,"requestAt",clock.ElapsedMilliseconds-900); controller.UpdateFeedback();
                check(controller.Feedback.IsShown && !controller.Panel.IsShown && controller.Feedback.Message.Contains("正在检查"),"a slow English request displays compact progress after the delay");
                controller.Panel.Dismiss(); delayed.SetResult("I have an apple."); await Task.Delay(80);
                check(!controller.Panel.IsShown && !controller.Feedback.IsShown && !controller.Panel.CanReplace,"closing the input card cancels progress and prevents a late result from reopening it");
            }
            delayed=new TaskCompletionSource<string>();
            using(var controller=Controller((text,cancel)=>delayed.Task,out monitor,out source)) {
                Start(controller,source); Set(monitor,"received",Stopwatch.GetTimestamp()-Stopwatch.Frequency*2);
                delayed.SetResult("I have an apple."); await Task.Delay(80);
                check(!controller.Panel.IsShown,"an old provider heartbeat cannot authorize publication even when the text matches");
            }
            var oldReply=new TaskCompletionSource<string>();
            using(var controller=Controller((text,cancel)=>text.Contains("New") ? Task.FromResult("New result.") : oldReply.Task,out monitor,out source)) {
                Start(controller,source);
                var newer=new InputSnapshot { Id=source.Id,Text="New original.",Editable=true,Mode=source.Mode,FocusHandle=source.FocusHandle };
                Set(monitor,"latest",newer); Fresh(monitor); Start(controller,newer); await Task.Delay(80);
                Set(controller,"replacing",true); oldReply.SetResult("Old result."); await Task.Delay(80);
                check((bool)typeof(TranslationController).GetField("replacing",Fields).GetValue(controller) && controller.Panel.English=="New result.","completion of a cancelled request cannot release another operation's replacement guard");
                Set(controller,"replacing",false);
            }
            using(var controller=Controller((text,cancel)=>RequestReliability.WithDeadlineAsync(token=>new TaskCompletionSource<string>().Task,50,cancel),out monitor,out source)) {
                Start(controller,source); await Wait(()=>controller.Panel.IsShown);
                check(controller.Panel.IsShown && controller.Panel.English.Contains("超时") && !controller.Panel.CanReplace && Retry(controller.Panel).Enabled,"a request deadline displays a retryable failure in the actual input controller");
            }
            attempts=0;
            using(var controller=Controller((text,cancel)=> { attempts++; throw new InvalidOperationException("retry fixture"); },out monitor,out source)) {
                Start(controller,source); await Wait(()=>controller.Panel.IsShown);
                var changed=new InputSnapshot { Id=source.Id,Text="Changed input",Editable=true,Mode=source.Mode,FocusHandle=source.FocusHandle };
                Set(monitor,"latest",changed); Fresh(monitor); controller.Panel.Retry();
                check(attempts==1 && !controller.Panel.IsShown,"retry after changing input dismisses the old card without sending another request");
            }
            if(online) {
                string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HoverLex","UserData");
                var settings=Settings.Load(Path.Combine(root,"settings.json"));
                TranslationPreferences.Configure(settings.TranslationProvider,settings.DeepSeekModel,ApiKeyStore.Load(root));
                var service=new OnlineTranslator();
                check(await service.TranslateAsync("I has an apple.",CancellationToken.None)=="I have an apple.","configured provider corrects a real English grammar error");
                check(await service.TranslateAsync("I am ready.",CancellationToken.None)=="I am ready.","configured provider preserves already correct English");
                check(await service.TranslateAsync("She go to school every day.",CancellationToken.None)=="She goes to school every day.","configured provider corrects a subject and verb agreement error");
                check(await service.TranslateAsync("I don't want to buy 3 apples.",CancellationToken.None)=="I don't want to buy 3 apples.","configured provider preserves negation and numbers");
                string technical="Use `recieve` and https://example.com/recieve with my_variable.";
                check(await service.TranslateAsync(technical,CancellationToken.None)==technical,"configured provider preserves inline code, a URL and an identifier");
            }
        }
    }
}
