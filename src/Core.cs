using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace HoverLex
{
    public static class Words
    {
        public static string Clean(string text)
        {
            string word = (text ?? "").Trim().Trim('"', '\u201c', '\u201d', '\u2018', '\u2019', '.', ',', ':', ';', '!', '?', '(', ')', '[', ']', '{', '}', '<', '>');
            word = word.Replace('\u2019', '\'');
            if (word.Length > 64 || !Regex.IsMatch(word, @"^[A-Za-z]+(?:[-'][A-Za-z]+)*$")) return "";
            return word;
        }

        public static IEnumerable<string> Candidates(string word)
        {
            string w = word.ToLowerInvariant();
            yield return w;
            if (w.EndsWith("'s")) yield return w.Substring(0, w.Length - 2);
            if (w.EndsWith("ies") && w.Length > 4) yield return w.Substring(0, w.Length - 3) + "y";
            if (w.EndsWith("ing") && w.Length > 5)
            {
                string stem = w.Substring(0, w.Length - 3);
                yield return stem; yield return stem + "e";
                if (stem.Length > 2 && stem[stem.Length - 1] == stem[stem.Length - 2]) yield return stem.Substring(0, stem.Length - 1);
            }
            if (w.EndsWith("ed") && w.Length > 3)
            {
                yield return w.Substring(0, w.Length - 1);
                string stem = w.Substring(0, w.Length - 2);
                yield return stem;
                if (stem.Length > 2 && stem[stem.Length - 1] == stem[stem.Length - 2]) yield return stem.Substring(0, stem.Length - 1);
            }
            if (w.EndsWith("es") && w.Length > 3) yield return w.Substring(0, w.Length - 2);
            if (w.EndsWith("s") && w.Length > 2) yield return w.Substring(0, w.Length - 1);
        }
    }

    public sealed class Entry
    {
        public string Word, Phonetic, Chinese, English, Tags, Exchange;
    }

    public sealed class LocalDictionary : IDisposable
    {
        private string[] words;
        private long[] positions;
        private BinaryReader data;
        public int Count { get { return words.Length; } }
        public LocalDictionary(string directory)
        {
            using (BinaryReader index = new BinaryReader(File.OpenRead(Path.Combine(directory, "dictionary.idx")), Encoding.UTF8))
            {
                if (Encoding.ASCII.GetString(index.ReadBytes(4)) != "HLX1") throw new InvalidDataException("词典索引无效");
                int count = index.ReadInt32();
                if (count < 1 || count > 3000000) throw new InvalidDataException("词条数无效");
                words = new string[count]; positions = new long[count];
                for (int i = 0; i < count; i++)
                {
                    words[i] = Encoding.UTF8.GetString(index.ReadBytes(index.ReadUInt16()));
                    positions[i] = index.ReadInt64();
                }
            }
            data = new BinaryReader(File.OpenRead(Path.Combine(directory, "dictionary.dat")), Encoding.UTF8);
        }

        private Entry Exact(string word)
        {
            int i = Array.BinarySearch(words, word, StringComparer.Ordinal);
            if (i < 0) return null;
            lock (data)
            {
                data.BaseStream.Position = positions[i];
                return new Entry { Word = data.ReadString(), Phonetic = data.ReadString(), Chinese = data.ReadString(), English = data.ReadString(), Tags = data.ReadString(), Exchange = data.ReadString() };
            }
        }

        public Entry Lookup(string word)
        {
            foreach (string candidate in Words.Candidates(word))
            {
                Entry entry = Exact(candidate);
                if (entry == null) continue;
                foreach (string exchange in (entry.Exchange ?? "").Split('/'))
                    if (exchange.StartsWith("0:"))
                    {
                        Entry lemma = Exact(exchange.Substring(2).ToLowerInvariant());
                        if (lemma != null && !String.IsNullOrWhiteSpace(lemma.Chinese)) return lemma;
                    }
                return entry;
            }
            return null;
        }

        public void Dispose() { if (data != null) data.Dispose(); }
    }

    public sealed class Settings
    {
        public bool Enabled = true;
        public int Delay = 350;
        public bool InputTranslation = false;
        public bool AutoReplaceTranslation = false;
        public bool EnglishCorrection = true;
        public bool ReviewReminder = true;
        public string LastReviewReminder = "";
        public string TranslationProvider = "free";
        public string DeepSeekModel = "deepseek-flash";
        public static Settings Load(string path)
        {
            try
            {
                Settings s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path, Encoding.UTF8));
                s.Delay = Math.Max(150, Math.Min(1200, s.Delay)); return s;
            }
            catch { return new Settings(); }
        }
        public void Save(string path) { Storage.WriteJson(path, this); }
    }

    public sealed class SavedWord
    {
        public string Id { get; set; }
        public string Word { get; set; }
        public string Lemma { get; set; }
        public string Meaning { get; set; }
        public string Context { get; set; }
        public string SavedAt { get; set; }
        public bool Archived { get; set; }
        public int ReviewStage { get; set; }
        public int ReviewCount { get; set; }
        public int Lapses { get; set; }
        public string NextReviewAt { get; set; }
        public string LastReviewAt { get; set; }
    }

    public static class Storage
    {
        public static void WriteJson(string path, object value)
        {
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(value), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        public static List<SavedWord> LoadWords(string path)
        {
            if (!File.Exists(path)) return new List<SavedWord>();
            // 文件损坏时保留原文件并明确报错，避免覆盖已保存的生词。
            return new JavaScriptSerializer().Deserialize<List<SavedWord>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<SavedWord>();
        }
        public static string Csv(string text) { return "\"" + (text ?? "").Replace("\"", "\"\"") + "\""; }
    }

    // 每次请求保留代号，鼠标移动或松键后丢弃旧结果。
    public sealed class HoverGate
    {
        public int Generation { get; private set; }
        private bool held, armed, asked;
        private Point anchor;
        private long restingSince, retryAfter;
        private int attempts;
        public bool Observe(Point point, bool ctrlOnly, bool enabled, bool blocked, long now, int delay, bool ready = true)
        {
            if (!ctrlOnly || !enabled || blocked)
            {
                if (held || armed) Generation++;
                held = false; armed = false; asked = false; attempts = 0; retryAfter = 0; anchor = point; restingSince = now;
                return false;
            }
            if (!held)
            {
                held = true; armed = true; anchor = point; restingSince = now; asked = false; attempts = 0; retryAfter = 0; return false;
            }
            if (Math.Abs(point.X - anchor.X) > 3 || Math.Abs(point.Y - anchor.Y) > 3)
            {
                Generation++; anchor = point; restingSince = now; armed = true; asked = false; attempts = 0; retryAfter = 0;
            }
            if (armed && !asked && ready && now >= retryAfter && now - restingSince >= delay)
            {
                asked = true; attempts++; return true;
            }
            return false;
        }
        public void Retry(int generation, long now)
        {
            if (generation != Generation || !held || !armed || !asked || attempts >= 3) return;
            asked = false; retryAfter = now + 450;
        }
        public void Reset() { Generation++; held = false; armed = false; asked = false; attempts = 0; retryAfter = 0; }
    }
}
