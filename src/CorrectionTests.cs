using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HoverLex
{
    public static class CorrectionTests
    {
        public static int Run(bool online)
        {
            List<string> lines=new List<string>(); Action<bool,string> check=(ok,name)=>lines.Add((ok ? "PASS " : "FAIL ")+name);
            try { Scenarios(check,online).GetAwaiter().GetResult(); }
            catch(Exception error) { check(false,error.ToString()); }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"correction-tests.txt"),lines,Encoding.UTF8);
            return lines.Any(line=>line.StartsWith("FAIL")) ? 1 : 0;
        }
        public static int RunToggle(bool online=false)
        {
            List<string> lines=new List<string>(); Action<bool,string> check=(ok,name)=>lines.Add((ok ? "PASS " : "FAIL ")+name);
            try { ToggleScenarios(check,online).GetAwaiter().GetResult(); }
            catch(Exception error) { check(false,error.ToString()); }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"correction-toggle-tests.txt"),lines,Encoding.UTF8);
            return lines.Any(line=>line.StartsWith("FAIL")) ? 1 : 0;
        }
        private static async Task ToggleScenarios(Action<bool,string> check,bool online)
        {
            int corrections=0,translations=0; string source="";
            OnlineTranslator service=new OnlineTranslator((text,cancel)=> { corrections++; return Task.FromResult(text.Replace("I has","I have")); },(text,direction,cancel)=> { translations++; source=text; return Task.FromResult(direction==TranslationDirection.EnglishToChinese ? "我有一个苹果。" : "I has an apple."); });
            string result=await service.TranslateAsync("你好 I has an apple.",CancellationToken.None);
            check(result=="I have an apple." && source=="你好 I have an apple." && corrections==2,"enabled correction checks both the source and English result");
            service.CorrectionEnabled=false; int before=corrections;
            result=await service.TranslateAsync("你好 I has an apple.",CancellationToken.None);
            check(result=="I has an apple." && source=="你好 I has an apple." && corrections==before,"disabled correction bypasses source and output checking, including prior cached results");
            result=await service.TranslateAsync("I has an apple.",TranslationDirection.EnglishToChinese,CancellationToken.None);
            check(result=="我有一个苹果。" && source=="I has an apple." && corrections==before,"English-to-Chinese translation stays available with correction off");
            int translatedBefore=translations;
            result=await service.TranslateAsync("I has an apple.",CancellationToken.None);
            check(result=="I has an apple." && corrections==before && translations==translatedBefore,"disabled pure English input is preserved without correction or translation requests");
            service.CorrectionEnabled=true;
            result=await service.TranslateAsync("你好 I has an apple.",CancellationToken.None);
            check(result=="I have an apple." && corrections==before+2,"reenabling correction restores source and output checking without off-mode cache leakage");
            OnlineTranslator unavailable=new OnlineTranslator((text,cancel)=> { throw new InvalidOperationException("unavailable checker"); },(text,direction,cancel)=>Task.FromResult("Translation works."));
            unavailable.CorrectionEnabled=false;
            check(await unavailable.TranslateAsync("翻译不依赖纠错引擎",CancellationToken.None)=="Translation works.","turning correction off keeps translation usable even when the checker is unavailable");
            TaskCompletionSource<string> pending=new TaskCompletionSource<string>(); int calls=0;
            OnlineTranslator stale=new OnlineTranslator((text,cancel)=>pending.Task,(text,direction,cancel)=> { calls++; return Task.FromResult("unexpected"); });
            Task<string> request=stale.TranslateAsync("中文 I has an apple.",CancellationToken.None);
            stale.CorrectionEnabled=false; pending.SetResult("中文 I have an apple."); bool cancelled=false;
            try { await request; } catch(OperationCanceledException) { cancelled=true; }
            check(cancelled && calls==0,"changing correction mode invalidates an in-flight source check before translation");
            pending=new TaskCompletionSource<string>(); corrections=0;
            stale=new OnlineTranslator((text,cancel)=> { corrections++; return Task.FromResult(text); },(text,direction,cancel)=>pending.Task);
            request=stale.TranslateAsync("等待翻译",CancellationToken.None);
            stale.CorrectionEnabled=false; pending.SetResult("Late result."); cancelled=false;
            try { await request; } catch(OperationCanceledException) { cancelled=true; }
            check(cancelled && corrections==1,"changing correction mode invalidates a pending translation before output checking");
            if(online) {
                OnlineTranslator live=new OnlineTranslator(); live.CorrectionEnabled=false;
                result=await live.TranslateAsync("你好，明天下午三点开会。",CancellationToken.None);
                check(result.Length>0 && !InputTranslationGate.HasChinese(result),"live Chinese-to-English translation works with correction off");
                result=await live.TranslateAsync("Hello, world.",TranslationDirection.EnglishToChinese,CancellationToken.None);
                check(InputTranslationGate.HasChinese(result),"live English-to-Chinese translation works with correction off");
                live.ClearCache();
            }
        }
        private static async Task Scenarios(Action<bool,string> check,bool online)
        {
            EnglishProofreader engine=new EnglishProofreader();
            foreach(var pair in new[] {
                Tuple.Create("recieve","receive"),
                Tuple.Create("She go to school every day.","She goes to school every day."),
                Tuple.Create("I has an apple.","I have an apple."),
                Tuple.Create("He don't like apples.","He doesn't like apples."),
                Tuple.Create("This are a test.","This is a test."),
                Tuple.Create("I don't want to buy 3 apples.","I don't want to buy 3 apples."),
                Tuple.Create("Hello, world.","Hello, world."),
                Tuple.Create("  I am ready.\r\n", "  I am ready.\r\n"),
                Tuple.Create("你好，I has an apple。", "你好，I have an apple。"),
                Tuple.Create("Use `recieve` and https://example.com/recieve with my_variable and OPENAI_API_KEY.","Use `recieve` and https://example.com/recieve with my_variable and OPENAI_API_KEY.")
            }) {
                string corrected=await engine.CorrectAsync(pair.Item1,CancellationToken.None);
                check(corrected==pair.Item2,"local correction: "+pair.Item1.Replace("\r","\\r").Replace("\n","\\n")+" -> "+corrected.Replace("\r","\\r").Replace("\n","\\n"));
            }
            string tokens="`recieve` https://example.com/recieve C:\\recieve\\file.txt my_variable OPENAI_API_KEY";
            check(!InputTranslationGate.HasInput(tokens),"technical tokens alone do not trigger correction");
            check(await engine.CorrectAsync("纯中文不需要启动英文引擎。",CancellationToken.None)=="纯中文不需要启动英文引擎。","Chinese text remains unchanged during correction");
            using(CancellationTokenSource cancellation=new CancellationTokenSource()) {
                cancellation.Cancel(); bool cancelled=false;
                try { await engine.CorrectAsync("recieve",cancellation.Token); } catch(OperationCanceledException) { cancelled=true; }
                check(cancelled,"cancelled corrections reject cached results");
            }
            using(CancellationTokenSource cancellation=new CancellationTokenSource()) {
                EnglishProofreader.StopWorker();
                cancellation.CancelAfter(150); bool cancelled=false;
                try { await engine.CorrectAsync("She go to school every day and recieve a letter.",cancellation.Token); } catch(OperationCanceledException) { cancelled=true; }
                check(cancelled && EnglishProofreader.WorkerProcessId==0,"in-flight local proofreading is cancelled and its owned worker exits");
            }
            int translations=0; string translatedInput="";
            OnlineTranslator service=new OnlineTranslator(engine.CorrectAsync,(text,direction,cancel)=> { translations++; translatedInput=text; return Task.FromResult(direction==TranslationDirection.EnglishToChinese ? "她每天上学。" : "I has an apple."); });
            string english=await service.TranslateAsync("She go to school every day.",CancellationToken.None);
            check(english=="She goes to school every day." && translations==0,"English-only input is corrected without calling the translation service");
            english=await service.TranslateAsync("你好，I has an apple。",CancellationToken.None);
            check(translatedInput=="你好，I have an apple。" && english=="I have an apple.","mixed input is corrected before translation and English output is checked again");
            string chinese=await service.TranslateAsync("She go to school every day.",TranslationDirection.EnglishToChinese,CancellationToken.None);
            check(chinese=="她每天上学。" && translatedInput=="She goes to school every day.","English-to-Chinese translation uses the corrected source");
            int before=translations;
            check(await service.TranslateAsync("你好，I has an apple。",CancellationToken.None)=="I have an apple." && translations==before,"completed correction and translation use the memory cache");
            service.ClearCache(); await service.TranslateAsync("你好，I has an apple。",CancellationToken.None);
            check(translations==before+1,"clearing the service cache clears completed corrected results");
            OnlineTranslator broken=new OnlineTranslator((text,cancel)=> { throw new InvalidOperationException("fixture failure"); },(text,direction,cancel)=> { translations++; return Task.FromResult("unexpected"); });
            before=translations; bool refused=false;
            try { await broken.TranslateAsync("I has an apple.",CancellationToken.None); } catch(InvalidOperationException) { refused=true; }
            check(refused && translations==before,"correction failure does not silently translate uncorrected input");
            if(online) {
                OnlineTranslator live=new OnlineTranslator();
                english=await live.TranslateAsync("我想说：I has an apple.",CancellationToken.None);
                check(!InputTranslationGate.HasChinese(english) && !english.Contains("I has"),"live mixed translation returns English with grammar correction: "+english);
                chinese=await live.TranslateAsync("She go to school every day.",TranslationDirection.EnglishToChinese,CancellationToken.None);
                check(InputTranslationGate.HasChinese(chinese),"live English-to-Chinese translation accepts corrected English");
                live.ClearCache();
            }
        }
    }
}
