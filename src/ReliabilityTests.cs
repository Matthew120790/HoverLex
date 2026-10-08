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
    internal static class ReliabilityTests
    {
        private sealed class PassiveFixture : Form { protected override bool ShowWithoutActivation { get { return true; } } protected override CreateParams CreateParams { get { var p=base.CreateParams; p.ExStyle|=0x08000000; return p; } } }
        private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
        private static void Set(object target,string name,object value) { target.GetType().GetField(name,Fields).SetValue(target,value); }
        private static Button Button(Control parent,string text) { foreach(Control child in parent.Controls) { if(child is Button && child.Text==text) return (Button)child; var found=Button(child,text); if(found!=null) return found; } return null; }
        private static Task NoWait(int delay,CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(0); }
        internal static int Run()
        {
            var lines=new List<string>(); Action<bool,string> check=(ok,name)=>lines.Add((ok ? "PASS " : "FAIL ")+name);
            using(var context=new ApplicationContext()) {
                EventHandler idle=null;
                idle=async (sender,args)=> {
                    Application.Idle-=idle;
                    try { Core(check); NativeAdapters(check); await Requests(check); await Panels(check); await MonitorLifecycle(check); } catch(Exception error) { check(false,error.ToString()); }
                    finally { context.ExitThread(); }
                };
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Application.Idle+=idle; Application.Run(context);
            }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"reliability-tests.txt"),lines,Encoding.UTF8);
            return lines.Any(line=>line.StartsWith("FAIL")) ? 1 : 0;
        }
        private static void Core(Action<bool,string> check)
        {
            var recovery=new InputRecovery(); recovery.Failed(0);
            check(!recovery.Due(999,false) && recovery.Due(1000,false),"failed reader waits before its first automatic restart");
            recovery.Healthy(1100); recovery.Healthy(1200); recovery.Failed(1500);
            check(recovery.Failures==2 && recovery.RetryAt==3500,"a single heartbeat cannot erase repeated reader failures");
            recovery.Failed(3500); recovery.Failed(7500);
            check(!recovery.Due(37499,false) && recovery.Due(37500,false),"persistent reader failures back off and retry after 30 seconds");
            check(recovery.Due(7600,true),"typing or changing focus can recover immediately during backoff");
            recovery.Healthy(40000); recovery.Healthy(45000);
            check(recovery.Failures==0,"five seconds of healthy input monitoring clears failure history");
            var monitor=(InputMonitor)FormatterServices.GetUninitializedObject(typeof(InputMonitor));
            Set(monitor,"responded",true); Set(monitor,"received",Stopwatch.GetTimestamp()-Stopwatch.Frequency*2);
            check(!monitor.Fresh,"stale input snapshots cannot authorize replacement or retry");
            Set(monitor,"received",Stopwatch.GetTimestamp()); check(monitor.Fresh,"a recent provider heartbeat restores snapshot freshness");
            monitor.Dispose(); check(!monitor.Fresh,"disposing a monitor permanently invalidates its snapshots");
            bool robust=true;
            for(int i=0;i<1000;i++) {
                var gate=new InputTranslationGate(); var empty=new InputSnapshot { Id="fixture",Editable=true,FocusHandle=42 };
                var text=new InputSnapshot { Id="fixture",Editable=true,FocusHandle=42,Text="I has an apple. "+i };
                gate.Observe(empty,0,0,true,42); gate.Observe(text,1,100,true,42);
                robust&=gate.Observe(text,1,600,true,42); gate.Observe(null,1,650,true,42);
                robust&=gate.Observe(text,1,900,true,42); gate.Complete(gate.Generation);
                robust&=!gate.Observe(text,1,1500,true,42);
                gate.Observe(null,1,1600,true,42); robust&=!gate.Observe(text,1,1800,true,42);
                gate.Reset(1); robust&=!gate.Observe(text,1,2000,true,42);
            }
            check(robust,"1000 interruption, recovery, completion and reset cycles retain each input without duplicate requests");
        }
        private static async Task Requests(Action<bool,string> check)
        {
            int calls=0;
            string result=await RequestReliability.RetryAsync(token=> { calls++; if(calls==1) throw new TranslationServiceException("busy",true); return Task.FromResult("OK"); },CancellationToken.None,NoWait);
            check(result=="OK" && calls==2,"temporary provider failure retries once and can recover");
            calls=0; bool failed=false;
            try { await RequestReliability.RetryAsync<string>(token=> { calls++; throw new TranslationServiceException("busy",true); },CancellationToken.None,NoWait); } catch(TranslationServiceException) { failed=true; }
            check(failed && calls==2,"persistent provider failure stops after two total attempts");
            calls=0; failed=false;
            try { await RequestReliability.RetryAsync<string>(token=> { calls++; throw new TranslationServiceException("invalid key",false); },CancellationToken.None,NoWait); } catch(TranslationServiceException) { failed=true; }
            check(failed && calls==1,"invalid credentials are reported without automatic retry");
            using(var stop=new CancellationTokenSource()) {
                calls=0; bool cancelled=false;
                try { await RequestReliability.RetryAsync<string>(token=> { calls++; throw new TranslationServiceException("busy",true); },stop.Token,(delay,token)=> { stop.Cancel(); return Task.FromResult(0); }); } catch(OperationCanceledException) { cancelled=true; }
                check(cancelled && calls==1,"continued typing can cancel a retry before another request is sent");
            }
            var pending=new TaskCompletionSource<string>(); bool stopped=false; failed=false;
            try { await RequestReliability.WithDeadlineAsync(token=> { token.Register(()=>stopped=true); return pending.Task; },50,CancellationToken.None); } catch(InvalidOperationException error) { failed=error.Message.Contains("超时"); }
            check(failed && stopped,"an unresponsive provider reaches a deadline and receives cancellation");
            pending.SetException(new InvalidOperationException("late failure"));
            using(var stop=new CancellationTokenSource()) {
                stop.CancelAfter(30); bool cancelled=false;
                try { await RequestReliability.WithDeadlineAsync(token=>new TaskCompletionSource<string>().Task,1000,stop.Token); } catch(OperationCanceledException) { cancelled=true; }
                check(cancelled,"user cancellation remains distinct from a provider timeout");
            }
            check(await RequestReliability.WithDeadlineAsync(token=>Task.FromResult("OK"),1000,CancellationToken.None)=="OK","deadline protection preserves an immediately completed result");
        }
        private static void NativeAdapters(Action<bool,string> check)
        {
            using(var fixture=new PassiveFixture { Bounds=new System.Drawing.Rectangle(10,10,600,210),ShowInTaskbar=false })
            using(var edit=new TextBox { Multiline=true,Bounds=new System.Drawing.Rectangle(10,10,550,70) })
            using(var rich=new RichTextBox { Bounds=new System.Drawing.Rectangle(10,90,550,70) })
            using(var secret=new TextBox { UseSystemPasswordChar=true,Text="fixture secret",Bounds=new System.Drawing.Rectangle(10,170,200,20) }) {
                fixture.Controls.AddRange(new Control[] { edit,rich,secret });
                IntPtr first=edit.Handle,second=rich.Handle,password=secret.Handle;
                fixture.Show();
                Native.SetWindowPos(fixture.Handle,new IntPtr(-1),0,0,0,0,0x0053);
                foreach(Control editor in fixture.Controls) Native.SetWindowPos(editor.Handle,IntPtr.Zero,0,0,0,0,0x0053);
                var read=typeof(InputReader).GetMethod("ReadNativeEdit",BindingFlags.Static|BindingFlags.NonPublic);
                bool exact=true,triggered=true;
                foreach(var editor in new Control[] { edit,rich }) {
                    var gate=new InputTranslationGate(); long serial=0,now=0;
                    editor.Text=""; var before=(InputSnapshot)read.Invoke(null,new object[] { editor.Handle }); gate.Observe(before,serial,now,true,before.FocusHandle);
                    for(int i=0;i<100;i++) {
                        editor.Text=i%3==0 ? "I has an apple." : i%3==1 ? "你好 I has an apple." : "recieve";
                        var snapshot=(InputSnapshot)read.Invoke(null,new object[] { editor.Handle }); exact&=snapshot.Editable && snapshot.Text==editor.Text;
                        if(i==0) check(snapshot.Editable && snapshot.Text==editor.Text,"native fixture is readable: "+editor.GetType().Name+" error="+snapshot.Error+" composing="+snapshot.Composing+" id="+snapshot.Id+" parentVisible="+fixture.Visible+" childVisible="+editor.Visible);
                        now+=1000; serial++; gate.Observe(snapshot,serial,now,true,snapshot.FocusHandle);
                        triggered&=gate.Observe(snapshot,serial,now+500,true,snapshot.FocusHandle);
                        gate.Complete(gate.Generation);
                    }
                    editor.Text=""; var empty=(InputSnapshot)read.Invoke(null,new object[] { editor.Handle }); gate.Observe(empty,++serial,now+1000,true,empty.FocusHandle);
                    triggered&=!gate.Observe(empty,serial,now+1600,true,empty.FocusHandle);
                }
                check(exact,"200 actual Edit and RichEdit reads retain English, mixed text and spelling errors exactly");
                check(triggered,"200 actual native text changes trigger checking, while clearing input never triggers a request");
                var protectedInput=(InputSnapshot)read.Invoke(null,new object[] { password });
                check(!protectedInput.Editable && protectedInput.Text.Length==0 && protectedInput.Error.Contains("密码"),"the real native password control remains excluded");
                edit.ReadOnly=true; var locked=(InputSnapshot)read.Invoke(null,new object[] { first });
                check(!locked.Editable,"the real native read-only control remains excluded");
            }
        }
        private static async Task MonitorLifecycle(Action<bool,string> check)
        {
            using(var monitor=new InputMonitor()) {
                for(int i=0;i<30 && !monitor.HasResponse;i++) await Task.Delay(100);
                check(monitor.Healthy && monitor.Fresh,"a real isolated input reader starts and reports recent heartbeats");
                var process=(Process)typeof(InputMonitor).GetField("process",Fields).GetValue(monitor);
                process.StandardInput.WriteLine("stop"); process.StandardInput.Flush();
                bool exited=await Task.Run(()=>process.WaitForExit(3000));
                check(exited && !monitor.Healthy,"normal reader exit is detected without keeping a false healthy state");
                monitor.Dispose(); check(!monitor.Fresh && monitor.Latest==null,"reader shutdown clears snapshots and freshness");
            }
            using(var monitor=new InputMonitor()) {
                for(int i=0;i<30 && !monitor.HasResponse;i++) await Task.Delay(100);
                check(monitor.Healthy && monitor.Fresh,"a fresh reader starts successfully after the previous child exits");
            }
        }
        private static async Task Panels(Action<bool,string> check)
        {
            var first=new InputSnapshot { Id="one",Text="Original",X=20,Y=20 };
            var second=new InputSnapshot { Id="two",Text="New original",X=20,Y=20 };
            using(var panel=new TranslationPanel()) {
                var replacement=new TaskCompletionSource<string>(); int applies=0;
                panel.ReplaceText=(source,text)=> { applies++; return replacement.Task; };
                panel.Result(first,"First result"); Task work=panel.ApplyReplacementAsync();
                await panel.ApplyReplacementAsync(); check(applies==1,"rapid replacement clicks cannot start two writes");
                panel.Pending(second); replacement.SetResult(""); await work;
                check(panel.English=="正在翻译…" && !panel.CanReplace && !Button(panel,"复制").Enabled,"a late replacement cannot enable buttons for a new pending result");
                replacement=new TaskCompletionSource<string>(); panel.Result(first,"First result"); work=panel.ApplyReplacementAsync();
                panel.Result(second,"Second result"); replacement.SetResult(""); await work;
                check(panel.English=="Second result" && panel.CanReplace && Button(panel,"重译").Enabled,"a late replacement receipt cannot overwrite the newer result");
                panel.Hide(); check(!panel.CanReplace,"hiding a card invalidates its replacement source");
                replacement=new TaskCompletionSource<string>(); panel.Result(first,"First result"); work=panel.ApplyReplacementAsync();
                panel.Failure("New error"); replacement.SetResult("old error"); await work;
                check(panel.English=="New error" && !Button(panel,"复制").Enabled && Button(panel,"重译").Enabled,"a late replacement failure cannot overwrite a newer error card");
            }
            using(var feedback=new InputFeedback()) {
                IntPtr focus=InputReader.FocusWindow(); feedback.Display(first,"检查完成 · 未发现明显英文错误",80);
                check(feedback.IsShown && InputReader.FocusWindow()==focus,"compact input feedback appears without stealing keyboard focus");
                await Task.Delay(150); check(!feedback.IsShown,"completed input feedback disappears automatically");
            }
            using(var input=new TextBox { Text="Hello." })
            using(var form=new Form()) {
                form.Controls.Add(input); IntPtr handle=input.Handle;
                var reply=new TaskCompletionSource<string>();
                using(var search=new SearchTranslation(input,message=>{},word=>null,word=>{},(text,direction,token)=>reply.Task)) {
                    Task query=search.QueryAsync(); search.Panel.Dismiss(); reply.SetResult("Late result"); await query;
                    check(!search.Panel.IsShown && !search.Panel.CanReplace,"closing a manual translation card cancels its pending result");
                }
            }
        }
    }
}
