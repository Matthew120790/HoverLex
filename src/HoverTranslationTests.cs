using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace HoverLex
{
    public static class HoverTranslationTests
    {
        private static string Response(string word,string meaning)
        {
            var json=new JavaScriptSerializer();
            return json.Serialize(new { choices=new[] { new { finish_reason="stop",message=new { content=json.Serialize(new { word=word,result=meaning }) } } } });
        }
        public static int Run(bool online)
        {
            List<string> lines=new List<string>(); Action<bool,string> check=(ok,name)=>lines.Add((ok ? "PASS " : "FAIL ")+name);
            TranslationOptions old=TranslationPreferences.Current;
            try { Scenarios(check).GetAwaiter().GetResult(); if(online) Live(check,lines).GetAwaiter().GetResult(); Views(check); MainFlow(check); }
            catch(Exception error) { check(false,error.ToString()); }
            finally { TranslationPreferences.Configure(old.Provider,old.Model,old.Key); }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"hover-translation-tests.txt"),lines,Encoding.UTF8);
            return lines.Any(line=>line.StartsWith("FAIL")) ? 1 : 0;
        }
        private static async Task Scenarios(Action<bool,string> check)
        {
            var capture=new CaptureResult { Word="bank",Context="We sat on the river bank.",Method="界面文字" };
            var offline=new Entry { Word="bank",Chinese="n. 银行\nn. 岸",Phonetic="bæŋk" };
            int calls=0; TranslationOptions received=null; string input="";
            var service=new HoverTranslation((body,options,cancel)=> { calls++; received=options; input=body; return Task.FromResult(Response("bank","n. 河岸（原句指河边的岸）")); });
            TranslationPreferences.Configure("free","deepseek-flash","");
            check(await service.LookupAsync(capture,offline,TranslationPreferences.Current,CancellationToken.None)==offline && calls==0,"free hover lookup returns the offline entry without a network call");
            TranslationPreferences.Configure("deepseek","deepseek-v4-pro","test-key");
            Entry entry=await service.LookupAsync(capture,offline,TranslationPreferences.Current,CancellationToken.None);
            check(calls==1 && received.Model=="deepseek-v4-pro" && received.Key=="test-key","hover uses the shared provider, model and key");
            check(entry.Chinese.Contains("河岸") && entry.Word=="bank" && entry.Phonetic==offline.Phonetic && offline.Chinese.Contains("银行"),"context meaning preserves offline lemma and phonetics without mutating dictionary data");
            var json=new JavaScriptSerializer(); var payload=json.Deserialize<Dictionary<string,object>>(input); var messages=(IList)payload["messages"];
            string source=((Dictionary<string,object>)messages[1])["content"].ToString();
            var user=json.Deserialize<Dictionary<string,object>>(source);
            check(user["word"].ToString()==capture.Word && user["context"].ToString()==capture.Context && user.Count==2,"request contains only the target word and nearby context");
            check(((Dictionary<string,object>)payload["thinking"])["type"].ToString()=="disabled" && Convert.ToInt32(payload["max_tokens"])==512,"hover limits output and disables thinking to reduce latency");
            check(((Dictionary<string,object>)messages[0])["content"].ToString().Contains("untrusted") && ((Dictionary<string,object>)messages[0])["content"].ToString().Contains("Never change or correct"),"source instructions are untrusted and the target word is not rewritten");
            await service.LookupAsync(capture,offline,TranslationPreferences.Current,CancellationToken.None);
            check(calls==1,"repeated same-word same-context lookup reuses the cache");
            capture.Context="I went to the bank to withdraw money."; await service.LookupAsync(capture,offline,TranslationPreferences.Current,CancellationToken.None);
            check(calls==2,"different contexts do not share a meaning cache entry");
            service.Clear(); await service.LookupAsync(capture,offline,TranslationPreferences.Current,CancellationToken.None);
            check(calls==3,"clearing hover cache causes a fresh lookup");
            TranslationPreferences.Configure("deepseek","deepseek-flash","another-key"); await service.LookupAsync(capture,offline,TranslationPreferences.Current,CancellationToken.None);
            check(calls==4 && received.Model=="deepseek-flash","changing settings invalidates previous cached answers");
            entry=await service.LookupAsync(capture,null,TranslationPreferences.Current,CancellationToken.None);
            check(entry.Word=="bank" && entry.Chinese.Contains("河岸") && entry.Phonetic=="","DeepSeek works for words missing from the local dictionary");
            string context=new string('x',2000)+" bank "+new string('y',2000); string nearby=HoverTranslation.NearbyContext("bank",context);
            check(nearby.Length<=800 && nearby.Contains(" bank "),"long context is clipped around the target word");
            check(HoverTranslation.NearbyContext("bank",null)=="","missing context is supported");
            foreach(string response in new[] { Response("banks","河岸"),Response("bank","river bank"),Response("bank",""),Response("bank",new string('岸',1001)),Response("bank","河岸").Replace("stop","length") }) {
                bool refused=false; try { HoverTranslation.Parse("bank",response); } catch { refused=true; }
                check(refused,"wrong target, invalid meaning or truncated output is refused");
            }
            var pending=new TaskCompletionSource<string>(); var delayed=new HoverTranslation((body,options,cancel)=>pending.Task);
            using(var stop=new CancellationTokenSource()) {
                Task<Entry> task=delayed.LookupAsync(capture,offline,TranslationPreferences.Current,stop.Token);
                stop.Cancel(); pending.SetResult(Response("bank","银行")); bool cancelled=false;
                try { await task; } catch(OperationCanceledException) { cancelled=true; }
                check(cancelled,"late results are discarded after request cancellation even if transport ignores cancellation");
            }
            pending=new TaskCompletionSource<string>(); Task<Entry> outdated=delayed.LookupAsync(capture,offline,TranslationPreferences.Current,CancellationToken.None);
            TranslationPreferences.Configure("free","deepseek-flash",""); pending.SetResult(Response("bank","银行")); bool stale=false;
            try { await outdated; } catch(OperationCanceledException) { stale=true; }
            check(stale,"switching translation settings discards an in-flight hover answer");
            using(var stop=new CancellationTokenSource()) {
                stop.Cancel(); int before=calls; bool cancelled=false;
                try { await service.LookupAsync(capture,offline,TranslationPreferences.Current,stop.Token); } catch(OperationCanceledException) { cancelled=true; }
                check(cancelled && before==calls,"pre-cancelled hover lookup makes no network request");
            }
            TranslationPreferences.Configure("deepseek","deepseek-flash",""); bool missing=false;
            try { await new HoverTranslation().LookupAsync(capture,offline,TranslationPreferences.Current,CancellationToken.None); } catch(InvalidOperationException error) { missing=error.Message.Contains("密钥"); }
            check(missing,"missing DeepSeek key produces an actionable error before network access");
        }
        private static void Views(Action<bool,string> check)
        {
            var capture=new CaptureResult { Word="bank",Context="We sat on the river bank.",Method="界面文字" };
            using(var panel=new LookupPanel()) {
                SelfTest.Prepare(panel); panel.StartPosition=FormStartPosition.Manual; panel.Location=new Point(-32000,-32000); panel.Show(); Application.DoEvents();
                panel.SetResult(capture,new Entry { Word="bank",Chinese="n. 银行",Phonetic="bæŋk" });
                panel.SetProviderState("DeepSeek · 正在解释词义…",null);
                ListBox meanings=panel.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<ListBox>().Single();
                check(meanings.Items[0].ToString()=="n. 银行","offline reference remains readable while the online lookup is pending");
                var result=new Entry { Word="bank",Chinese="n. 河岸（原句指河边的岸）",Phonetic="bæŋk" };
                panel.SetResult(capture,result); panel.SetProviderState("DeepSeek · 语境释义",null);
                bool saved=false; panel.SaveWord=(original,found,chosen)=> { saved=original==capture && found==result && chosen==result.Chinese; return true; };
                Button save=panel.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<Button>().Single(button=>button.Text=="收藏词义");
                save.PerformClick(); check(saved,"saving AI meaning retains the original captured word and context");
                using(Bitmap image=new Bitmap(panel.Width,panel.Height)) { panel.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size)); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"hover-deepseek-preview.png")); }
                panel.SetResult(capture,null); panel.SetProviderState("DeepSeek · 查询未完成","请在翻译设置中输入 DeepSeek API 密钥");
                check(!save.Enabled,"failed lookup of an unknown word cannot save an error message as a meaning");
                panel.Close();
            }
        }
        private static async Task Live(Action<bool,string> check,List<string> lines)
        {
            string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"HoverLex","UserData");
            string key=ApiKeyStore.Load(directory);
            if(String.IsNullOrWhiteSpace(key)) { lines.Add("SKIP live DeepSeek: no configured API key"); return; }
            Settings settings=Settings.Load(Path.Combine(directory,"settings.json"));
            TranslationPreferences.Configure("deepseek",settings.DeepSeekModel,key);
            var service=new HoverTranslation();
            foreach(var example in new[] { Tuple.Create("We sat on the river bank.","岸"),Tuple.Create("I went to the bank to deposit money.","银行") }) {
                var watch=System.Diagnostics.Stopwatch.StartNew();
                Entry result=await service.LookupAsync(new CaptureResult { Word="bank",Context=example.Item1 },null,TranslationPreferences.Current,CancellationToken.None);
                lines.Add("INFO live DeepSeek hover "+watch.ElapsedMilliseconds+" ms: "+result.Chinese);
                check(result.Chinese.Contains(example.Item2),"live DeepSeek differentiates the target word using context: "+example.Item1);
            }
        }
        private static void MainFlow(Action<bool,string> check)
        {
            const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var pending=new TaskCompletionSource<string>();
            using(var main=new MainForm(true,new List<SavedWord>())) {
                main.CreateControl();
                Type type=typeof(MainForm);
                type.GetField("dictionary",flags).SetValue(main,new LocalDictionary(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Dictionary")));
                type.GetField("hoverTranslation",flags).SetValue(main,new HoverTranslation((body,options,cancel)=>pending.Task));
                type.GetField("previewMode",flags).SetValue(main,false);
                TranslationPreferences.Configure("deepseek","deepseek-flash","fixture-key");
                var capture=new CaptureResult { Word="bank",Context="We sat on the river bank.",Method="界面文字" };
                var popup=(LookupPanel)type.GetField("popup",flags).GetValue(main);
                MethodInfo show=type.GetMethod("ShowLookup",flags),dismiss=type.GetMethod("DismissHoverLookup",flags);
                Func<string> meanings=()=>String.Join("\n",popup.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<ListBox>().Single().Items.Cast<string>());
                show.Invoke(main,new object[] { capture,new Point(100,100),true });
                check(popup.Visible && meanings().Contains("银行"),"main mouse-lookup flow displays the offline card before the online response");
                dismiss.Invoke(main,null); pending.SetResult(Response("bank","旧释义")); Application.DoEvents();
                check(!popup.Visible && !meanings().Contains("旧释义"),"dismissing the hover card prevents a delayed response from reopening or changing it");
                pending=new TaskCompletionSource<string>(); show.Invoke(main,new object[] { capture,new Point(100,100),true });
                pending.SetResult(Response("bank","n. 河岸"));
                for(int i=0;i<100 && !meanings().Contains("河岸");i++) { Application.DoEvents(); Thread.Sleep(10); }
                check(popup.Visible && meanings()=="n. 河岸","main mouse-lookup flow applies the shared provider response to the visible card");
                dismiss.Invoke(main,null); type.GetField("previewMode",flags).SetValue(main,true);
            }
        }
    }
}
