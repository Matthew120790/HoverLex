using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace HoverLex
{
    public enum Recall { Forgot, Remembered, Easy }
    public static class Vocabulary
    {
        private static readonly int[] days = { 1, 3, 7, 14, 30, 60 };
        public static int NextIntervalDays(SavedWord word,Recall recall)
        {
            int stage=Math.Min(days.Length,Math.Max(0,Math.Min(days.Length,word.ReviewStage))+(recall==Recall.Easy ? 2 : 1));
            return days[Math.Max(0,stage-1)];
        }
        public static void EnsureIds(IEnumerable<SavedWord> words)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (SavedWord word in words) if (String.IsNullOrWhiteSpace(word.Id) || !ids.Add(word.Id)) { word.Id = Guid.NewGuid().ToString("N"); ids.Add(word.Id); }
        }
        public static List<SavedWord> Copy(IEnumerable<SavedWord> words)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            return json.Deserialize<List<SavedWord>>(json.Serialize(words.ToList()));
        }
        public static bool Due(SavedWord word, DateTimeOffset now)
        {
            DateTimeOffset due;
            return !word.Archived && (!DateTimeOffset.TryParse(word.NextReviewAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out due) || due <= now);
        }
        public static IEnumerable<SavedWord> Find(IEnumerable<SavedWord> words, string query, int filter, DateTimeOffset now)
        {
            query = (query ?? "").Trim();
            return words.Where(word => (filter == 2 ? word.Archived : !word.Archived) && (filter != 1 || Due(word, now)) &&
                new[] { word.Word, word.Lemma, word.Meaning, word.Context }.Any(value => (value ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0));
        }
        public static void Grade(SavedWord word, Recall recall, DateTimeOffset now)
        {
            if (word.Archived) throw new InvalidOperationException("请先恢复这个生词再复习");
            word.ReviewStage = Math.Max(0, Math.Min(days.Length, word.ReviewStage));
            word.ReviewCount = Math.Max(0, word.ReviewCount) + 1;
            word.LastReviewAt = now.ToUniversalTime().ToString("o");
            DateTimeOffset next;
            if (recall == Recall.Forgot) { word.ReviewStage = 0; word.Lapses = Math.Max(0, word.Lapses) + 1; next = now.AddMinutes(10); }
            else {
                word.ReviewStage = Math.Min(days.Length, word.ReviewStage + (recall == Recall.Easy ? 2 : 1));
                next = now.AddDays(days[Math.Max(0, word.ReviewStage - 1)]);
            }
            word.NextReviewAt = next.ToUniversalTime().ToString("o");
        }
        public static string Status(SavedWord word, DateTimeOffset now)
        {
            if (word.Archived) return "已归档";
            if (word.ReviewCount == 0) return "新词 · 待复习";
            if (Due(word, now)) return "到期 · 待复习";
            DateTimeOffset date;
            return DateTimeOffset.TryParse(word.NextReviewAt, out date) ? date.ToLocalTime().ToString("MM-dd HH:mm") : "待复习";
        }
    }
}
