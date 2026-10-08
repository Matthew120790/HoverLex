using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HoverLex
{
    public sealed class EnglishProofreader
    {
        private static readonly SemaphoreSlim serial=new SemaphoreSlim(1,1);
        private static string runtime;
        private static Process worker;
        private static readonly object workerLock=new object();
        private static long lastUse;
        private static readonly System.Threading.Timer idleTimer=new System.Threading.Timer(delegate { if(serial.Wait(0)) { try { if(Stopwatch.GetTimestamp()-lastUse>Stopwatch.Frequency*180) StopWorker(); } finally { serial.Release(); } } },null,60000,60000);
        static EnglishProofreader() { AppDomain.CurrentDomain.ProcessExit+=delegate { StopWorker(); }; }
        internal static int WorkerProcessId { get { lock(workerLock) return worker!=null && !worker.HasExited ? worker.Id : 0; } }
        public static Task WarmupAsync()
        {
            return Task.Run(()=> { serial.Wait(); try { EnsureWorker(CancellationToken.None); } catch { StopWorker(); } finally { serial.Release(); } });
        }
        public static void StopWorker()
        {
            lock(workerLock) { if(worker==null) return; try { if(!worker.HasExited) worker.Kill(); } catch(InvalidOperationException) { } finally { worker.Dispose(); worker=null; } }
        }
        private readonly Dictionary<string,string> cache=new Dictionary<string,string>();
        // 保留代码、链接、路径和常见标识符，掩码长度与原文相同。
        private static readonly Regex protectedText=new Regex(@"```[\s\S]*?(?:```|$)|`[^`\r\n]*(?:`|$)|https?://[^\s<>]+|\b[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}\b|\b[A-Za-z]:[\\/][^\r\n]+|\b[\w]+_[\w_]+\b|\b[A-Z][A-Z0-9]{1,}\b|\b[a-z]+[A-Z][A-Za-z0-9]*\b",RegexOptions.Compiled);
        public void ClearCache() { lock(cache) cache.Clear(); }
        internal static string Mask(string text)
        {
            char[] value=text.ToCharArray();
            foreach(Match match in protectedText.Matches(text)) for(int i=match.Index;i<match.Index+match.Length;i++) if(value[i]!='\r' && value[i]!='\n') value[i]=' ';
            for(int i=0;i<value.Length;i++) if(InputTranslationGate.HasChinese(value[i].ToString()) || "，。！？；：、（）【】《》".IndexOf(value[i])>=0) value[i]='\n';
            return new string(value);
        }
        public async Task<string> CorrectAsync(string text,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if(String.IsNullOrWhiteSpace(text) || !Regex.IsMatch(Mask(text),"[A-Za-z]")) return text;
            string found; lock(cache) if(cache.TryGetValue(text,out found)) return found;
            await serial.WaitAsync(cancellation).ConfigureAwait(false);
            try {
                lock(cache) if(cache.TryGetValue(text,out found)) return found;
                string corrected=text;
                for(int pass=0;pass<2;pass++) {
                    string mask=Mask(corrected);
                    string json;
                    try { json=await Task.Run(()=>Check(mask,cancellation),cancellation).ConfigureAwait(false); }
                    catch { cancellation.ThrowIfCancellationRequested(); throw; }
                    string next=Apply(corrected,mask,json);
                    if(next==corrected) break;
                    corrected=next;
                }
                cancellation.ThrowIfCancellationRequested();
                lock(cache) { if(cache.Count>=64) cache.Clear(); cache[text]=corrected; }
                return corrected;
            } finally { serial.Release(); }
        }
        private static string Prepare(CancellationToken cancellation)
        {
            if(runtime!=null) return runtime;
            string package=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"EnglishProofreader.zip");
            if(!File.Exists(package)) throw new InvalidOperationException("本机英文纠错组件缺失，请安装完整版");
            using(Stream resource=File.OpenRead(package)) {
                string hash; using(SHA256 sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(resource)).Replace("-","").Substring(0,16);
                string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Proofreader",hash);
                string marker=Path.Combine(folder,"ready.txt");
                if(!File.Exists(marker)) {
                    // 不覆盖半途解压的目录；并发进程各自使用完整的独立副本。
                    if(Directory.Exists(folder)) folder+="-"+Guid.NewGuid().ToString("N");
                    Directory.CreateDirectory(folder); resource.Position=0;
                    using(ZipArchive archive=new ZipArchive(resource,ZipArchiveMode.Read,true)) {
                        foreach(ZipArchiveEntry entry in archive.Entries) {
                            cancellation.ThrowIfCancellationRequested();
                            if(entry.FullName.EndsWith("/")) continue;
                            string target=Path.GetFullPath(Path.Combine(folder,entry.FullName));
                            if(!target.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("纠错组件路径无效");
                            Directory.CreateDirectory(Path.GetDirectoryName(target));
                            using(Stream input=entry.Open()) using(Stream output=new FileStream(target,FileMode.CreateNew)) input.CopyTo(output);
                        }
                    }
                    File.WriteAllText(Path.Combine(folder,"ready.txt"),hash);
                }
                runtime=folder; return folder;
            }
        }
        private static Process EnsureWorker(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            lock(workerLock) if(worker!=null && !worker.HasExited) { lastUse=Stopwatch.GetTimestamp(); return worker; }
            StopWorker();
            string folder=Prepare(cancellation);
            ProcessStartInfo start=new ProcessStartInfo {
                FileName=Path.Combine(folder,"java","bin","java.exe"),
                Arguments="-Xmx384m -Dfile.encoding=UTF-8 -cp \""+Path.Combine(folder,"tool","worker")+";"+Path.Combine(folder,"tool","libs","*")+"\" HoverLexProofreader",
                WorkingDirectory=Path.Combine(folder,"tool"),UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
            };
            Process process=new Process { StartInfo=start };
            try {
                process.Start(); process.ErrorDataReceived+=delegate { }; process.BeginErrorReadLine();
                lock(workerLock) worker=process;
                string ready=ReadReply(process,cancellation);
                if(ready!="{\"ready\":true}") throw new InvalidOperationException("本机英文纠错初始化失败，请重试");
                lastUse=Stopwatch.GetTimestamp(); return process;
            } catch { StopWorker(); throw; }
        }
        private static string ReadReply(Process process,CancellationToken cancellation)
        {
            using(CancellationTokenSource timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using(timeout.Token.Register(()=> { try { if(!process.HasExited) process.Kill(); } catch(InvalidOperationException) { } })) {
                timeout.CancelAfter(25000);
                string json=process.StandardOutput.ReadLine();
                cancellation.ThrowIfCancellationRequested();
                if(timeout.IsCancellationRequested) throw new InvalidOperationException("英文纠错超时，保留原输入，请重试");
                if(String.IsNullOrWhiteSpace(json) || json.Length>1024*1024) throw new InvalidOperationException("本机英文纠错未完成，保留原输入，请重试");
                return json;
            }
        }
        private static string Check(string text,CancellationToken cancellation)
        {
            Process process=EnsureWorker(cancellation);
            try {
                cancellation.ThrowIfCancellationRequested();
                string request=new JavaScriptSerializer().Serialize(new Dictionary<string,string> { { "text",text } })+"\n";
                byte[] bytes=Encoding.UTF8.GetBytes(request);
                process.StandardInput.BaseStream.Write(bytes,0,bytes.Length); process.StandardInput.BaseStream.Flush();
                string json=ReadReply(process,cancellation); lastUse=Stopwatch.GetTimestamp(); return json;
            } catch { StopWorker(); cancellation.ThrowIfCancellationRequested(); throw; }
        }
        internal static bool PreservesFacts(string before,string after)
        {
            var numbers=Regex.Matches(before,@"\d+(?:[.,]\d+)*").Cast<Match>().Select(m=>m.Value);
            var changed=Regex.Matches(after,@"\d+(?:[.,]\d+)*").Cast<Match>().Select(m=>m.Value);
            return numbers.SequenceEqual(changed) && Regex.Matches(before,@"\b(?:not|never|no)\b|n['’]t\b",RegexOptions.IgnoreCase).Count==Regex.Matches(after,@"\b(?:not|never|no)\b|n['’]t\b",RegexOptions.IgnoreCase).Count;
        }
        internal static string Apply(string text,string mask,string json)
        {
            var body=new JavaScriptSerializer { MaxJsonLength=1024*1024 }.Deserialize<Dictionary<string,object>>(json);
            object value; if(body==null || !body.TryGetValue("matches",out value) || !(value is IList)) throw new InvalidDataException("英文纠错结果无效");
            List<Tuple<int,int,string>> changes=new List<Tuple<int,int,string>>();
            foreach(var item in (IList)value) {
                var match=item as Dictionary<string,object>; if(match==null) throw new InvalidDataException("英文纠错结果无效");
                int offset=Convert.ToInt32(match["offset"]),length=Convert.ToInt32(match["length"]);
                var rule=match["rule"] as Dictionary<string,object>; string type=rule==null ? "" : Convert.ToString(rule["issueType"]);
                string id=rule==null ? "" : Convert.ToString(rule["id"]);
                if(type!="grammar" && type!="misspelling" && id!="I_LOWERCASE" && id!="ENGLISH_WORD_REPEAT_RULE") continue;
                if(offset<0 || length<=0 || offset+length>text.Length || text.Substring(offset,length)!=mask.Substring(offset,length) || !Regex.IsMatch(mask.Substring(offset,length),"[A-Za-z]")) continue;
                var replacements=match["replacements"] as IList; if(replacements==null || replacements.Count==0) continue;
                var replacement=replacements[0] as Dictionary<string,object>;
                string next=replacement==null ? null : Convert.ToString(replacement["value"]);
                // 语法建议有多种时，优先保留主语，避免把单数改成复数。
                Match subject=Regex.Match(text.Substring(offset,length),@"^(?:this|that|these|those|I|you|he|she|it|we|they)\b",RegexOptions.IgnoreCase);
                if(type=="grammar" && subject.Success) {
                    foreach(object suggestion in replacements) {
                        var candidate=suggestion as Dictionary<string,object>;
                        string proposed=candidate==null ? "" : Convert.ToString(candidate["value"]);
                        if(Regex.IsMatch(proposed,"^"+Regex.Escape(subject.Value)+@"\b",RegexOptions.IgnoreCase)) { next=proposed; break; }
                    }
                    if(length==subject.Length && next!=null && !String.Equals(next,subject.Value,StringComparison.OrdinalIgnoreCase) && HasVerbAlternative((IList)value,text,offset,length)) continue;
                }
                if(next==null || next.Length>2000 || InputTranslationGate.HasChinese(next) || !PreservesFacts(text.Substring(offset,length),next)) continue;
                if(changes.Any(c=>offset<c.Item1+c.Item2 && c.Item1<offset+length)) continue;
                changes.Add(Tuple.Create(offset,length,next));
            }
            StringBuilder result=new StringBuilder(text);
            foreach(var change in changes.OrderByDescending(c=>c.Item1)) { result.Remove(change.Item1,change.Item2); result.Insert(change.Item1,change.Item3); }
            return result.ToString();
        }
        private static bool HasVerbAlternative(IList matches,string text,int offset,int length)
        {
            foreach(object item in matches) {
                var match=item as Dictionary<string,object>; if(match==null) continue;
                int start=Convert.ToInt32(match["offset"]),count=Convert.ToInt32(match["length"]);
                if(start<offset+length || start>offset+length+24 || count<=0 || start+count>text.Length) continue;
                var rule=match["rule"] as Dictionary<string,object>;
                if(rule==null || Convert.ToString(rule["issueType"])!="grammar") continue;
                if(Regex.IsMatch(text.Substring(offset+length,start-offset-length),@"[.!?\r\n]")) continue;
                if(Regex.IsMatch(text.Substring(start,count),@"^(?:am|is|are|was|were|has|have|do|does|don't|doesn't)$",RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }
    }
}
