using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Linq;
using System.IO;

namespace HoverLex
{
    internal sealed class TranslationSegment
    {
        public string Text;
        public bool Literal;
        public string Before="",After="";
        public static List<TranslationSegment> Split(string text)
        {
            List<TranslationSegment> segments=new List<TranslationSegment>(); int position=0;
            foreach(Match token in TranslationText.Technical.Matches(text)) {
                Plain(text.Substring(position,token.Index-position),segments);
                segments.Add(new TranslationSegment { Text=token.Value,Literal=true }); position=token.Index+token.Length;
            }
            Plain(text.Substring(position),segments); return segments;
        }
        public static List<TranslationSegment> Paragraphs(string text)
        {
            List<TranslationSegment> segments=new List<TranslationSegment>(); Plain(text,segments); return segments;
        }
        private static void Plain(string text,List<TranslationSegment> segments)
        {
            foreach(string paragraph in Regex.Split(text,@"(\r\n|\r|\n)")) {
                if(paragraph.Length==0) continue;
                if(String.IsNullOrWhiteSpace(paragraph)) { segments.Add(new TranslationSegment { Text=paragraph,Literal=true }); continue; }
                foreach(string chunk in OnlineTranslator.Chunks(paragraph)) {
                    string body=chunk.Trim();
                    if(body.Length==0 || !Regex.IsMatch(body,@"[\p{L}\p{N}]")) { segments.Add(new TranslationSegment { Text=chunk,Literal=true }); continue; }
                    segments.Add(new TranslationSegment { Text=body,Before=Regex.Match(chunk,@"^\s*").Value,After=Regex.Match(chunk,@"\s*$").Value });
                }
            }
        }
    }
    internal sealed class ProtectedTranslation
    {
        internal static readonly Regex Marker=new Regex(@"HLX[A-F0-9]{12}Q\d+X",RegexOptions.Compiled);
        private readonly Dictionary<string,string> tokens=new Dictionary<string,string>();
        public readonly string Text;
        public ProtectedTranslation(string source)
        {
            string prefix; int salt=0;
            do { using(SHA256 hash=SHA256.Create()) prefix="HLX"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(source+":"+salt++))).Replace("-","").Substring(0,12)+"Q"; } while(source.Contains(prefix));
            Text=TranslationText.Technical.Replace(source,match=> { string token=prefix+tokens.Count+"X"; tokens.Add(token,match.Value); return token; });
        }
        public string Restore(string translated)
        {
            foreach(var token in tokens) {
                var pattern=new Regex(Regex.Escape(token.Key),RegexOptions.IgnoreCase);
                if(pattern.Matches(translated).Count!=1) throw new InvalidDataException("免费译文未保留技术文本，请重试或切换 DeepSeek");
                translated=pattern.Replace(translated,match=>token.Value);
            }
            return translated;
        }
    }
}
