using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HoverLex
{
    public static class QualityTests
    {
        public static int Run(bool online)
        {
            List<string> lines=new List<string>(); Action<bool,string> check=(ok,name)=>lines.Add((ok ? "PASS " : "FAIL ")+name);
            try { Core(check); Scenarios(check,lines,online).GetAwaiter().GetResult(); }
            catch(Exception error) { check(false,error.ToString()); }
            finally { EnglishProofreader.StopWorker(); }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"quality-tests.txt"),lines,Encoding.UTF8);
            return lines.Any(line=>line.StartsWith("FAIL")) ? 1 : 0;
        }
        private static void Core(Action<bool,string> check)
        {
            check(!EnglishProofreader.PreservesFacts("I do not buy 3 apples.","I buy 3 apples.") && !EnglishProofreader.PreservesFacts("3 apples","4 apples"),"proofreading rejects changes to negation or numbers");
            check(EnglishProofreader.PreservesFacts("He don't buy 3 apples.","He doesn't buy 3 apples."),"grammar corrections may retain negation through contractions");
            string sample="译文一\r\n\r\nUse `recieve` and https://example.com/my_file\n第二段。";
            var segments=TranslationSegment.Split(sample);
            check(String.Join("",segments.Select(segment=>segment.Before+segment.Text+segment.After))==sample,"free-mode segmentation preserves paragraphs and exact source boundaries");
            check(segments.Where(segment=>segment.Literal).Any(segment=>segment.Text=="`recieve`") && segments.Where(segment=>segment.Literal).Any(segment=>segment.Text=="https://example.com/my_file"),"free translation bypasses code and URLs verbatim");
            check(TranslationSegment.Split("。!?\n；").All(segment=>segment.Literal),"punctuation-only fragments are never submitted as sentences");
            ProtectedTranslation protectedInput=new ProtectedTranslation("请保留 `my_variable` 和 https://example.com。\n下一段。");
            check(protectedInput.Restore(protectedInput.Text)=="请保留 `my_variable` 和 https://example.com。\n下一段。","technical placeholders restore the exact original bytes");
            bool missingMarker=false; try { protectedInput.Restore("No markers"); } catch(InvalidDataException) { missingMarker=true; }
            check(missingMarker,"missing placeholders are rejected instead of damaging identifiers");
            string sentence=String.Join(" ",Enumerable.Repeat("learning",90)); var chunks=OnlineTranslator.Chunks(sentence);
            check(String.Join("",chunks)==sentence && chunks.All(chunk=>Encoding.UTF8.GetByteCount(chunk)<=480) && chunks.Take(chunks.Count-1).All(chunk=>Char.IsWhiteSpace(chunk[chunk.Length-1])),"long English sentences split on word boundaries within the service limit");
            bool refused=false; try { TranslationText.ValidateTokens("Use OPENAI_API_KEY","Use a key"); } catch(InvalidDataException) { refused=true; }
            check(refused,"DeepSeek results with missing technical identifiers are rejected");
            var json=new JavaScriptSerializer(); var payload=json.Deserialize<Dictionary<string,object>>(DeepSeekTranslation.Payload("请翻译",TranslationDirection.ChineseToEnglish,true,"deepseek-flash"));
            check(payload["model"].ToString()=="deepseek-flash" && ((Dictionary<string,object>)payload["thinking"])["type"].ToString()=="disabled" && ((Dictionary<string,object>)payload["response_format"])["type"].ToString()=="json_object","DeepSeek requests disable thinking and request structured output");
            string response="{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"{\\\"result\\\":\\\"Hello.\\\"}\"}}]}";
            check(DeepSeekTranslation.Parse(response)=="Hello.","DeepSeek structured responses return only the processed text");
            foreach(string bad in new[] { response.Replace("stop","length"),"{\"choices\":[]}",response.Replace("Hello.","") }) {
                refused=false; try { DeepSeekTranslation.Parse(bad); } catch { refused=true; } check(refused,"invalid or truncated DeepSeek output is refused");
            }
            check(DeepSeekTranslation.HttpError(401).Contains("密钥") && DeepSeekTranslation.HttpError(402).Contains("余额") && DeepSeekTranslation.HttpError(429).Contains("频繁"),"provider failures have actionable messages without exposing response bodies or keys");
            string directory=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"TestArtifacts","key-store-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            ApiKeyStore.Save(directory,"fixture-secret-123");
            check(ApiKeyStore.Load(directory)=="fixture-secret-123" && !Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(directory,"deepseek-key.dat"))).Contains("fixture-secret-123"),"API keys round-trip through Windows user encryption without plaintext storage");
        }
        private static async Task Scenarios(Action<bool,string> check,List<string> lines,bool online)
        {
            EnglishProofreader engine=new EnglishProofreader();
            var corpus=new[] {
                Tuple.Create("recieve","receive"),Tuple.Create("I has an apple.","I have an apple."),Tuple.Create("She go to school every day.","She goes to school every day."),
                Tuple.Create("He don't like apples.","He doesn't like apples."),Tuple.Create("This are a test.","This is a test."),Tuple.Create("I don't want to buy 3 apples.","I don't want to buy 3 apples."),
                Tuple.Create("The meeting starts at 3:30 PM.","The meeting starts at 3:30 PM."),Tuple.Create("Please do not delete 12 files.","Please do not delete 12 files."),
                Tuple.Create("Use `recieve` and my_variable.","Use `recieve` and my_variable."),Tuple.Create("I am ready.","I am ready.")
            };
            int worker=0;
            foreach(var pair in corpus) {
                Stopwatch clock=Stopwatch.StartNew(); string result=await engine.CorrectAsync(pair.Item1,CancellationToken.None);
                lines.Add("INFO proofreading "+clock.ElapsedMilliseconds+" ms"); check(result==pair.Item2,"quality corpus: "+pair.Item1+" -> "+result);
                if(worker==0) worker=EnglishProofreader.WorkerProcessId; else check(EnglishProofreader.WorkerProcessId==worker,"different sentences reuse the same initialized worker");
            }
            TaskCompletionSource<string> pending=new TaskCompletionSource<string>(); int calls=0;
            OnlineTranslator service=new OnlineTranslator((text,cancel)=>Task.FromResult(text),(text,direction,cancel)=> { calls++; return pending.Task; });
            Task<string> request=service.TranslateAsync("等待翻译",CancellationToken.None); TranslationOptions old=TranslationPreferences.Current;
            TranslationPreferences.Configure("free","deepseek-flash",""); pending.SetResult("Late result."); bool cancelled=false;
            try { await request; } catch(OperationCanceledException) { cancelled=true; }
            check(cancelled,"changing translation settings rejects in-flight results from the old provider");
            TranslationPreferences.Configure(old.Provider,old.Model,old.Key);
            using(CancellationTokenSource stop=new CancellationTokenSource()) {
                stop.Cancel(); cancelled=false; try { await DeepSeekTranslation.TranslateAsync("你好",TranslationDirection.ChineseToEnglish,true,new TranslationOptions(),stop.Token); } catch(OperationCanceledException) { cancelled=true; }
                check(cancelled,"cancelled DeepSeek operations do not send a request");
            }
            bool missing=false; try { await DeepSeekTranslation.TranslateAsync("你好",TranslationDirection.ChineseToEnglish,true,new TranslationOptions(),CancellationToken.None); } catch(InvalidOperationException) { missing=true; }
            check(missing,"DeepSeek requires a configured key before contacting the service");
            if(online) {
                OnlineTranslator free=new OnlineTranslator(); free.CorrectionEnabled=false;
                Stopwatch clock=Stopwatch.StartNew(); string result=await free.TranslateAsync("你好，世界！\n请保留 `my_variable`。",CancellationToken.None);
                lines.Add("INFO live free translation "+clock.ElapsedMilliseconds+" ms: "+result); check(result.Contains("\n") && result.Contains("`my_variable`") && !InputTranslationGate.HasChinese(result),"live free translation preserves paragraph breaks and literal identifiers");
            }
        }
    }
}
