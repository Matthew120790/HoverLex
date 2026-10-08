using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace HoverLex
{
    public static class LearningTests
    {
        public static int Run()
        {
            List<string> lines=new List<string>(); Action<bool,string> check=(ok,name)=>lines.Add((ok ? "PASS " : "FAIL ")+name);
            try { Core(check); Views(check); }
            catch(Exception error) { check(false,error.ToString()); }
            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"learning-tests.txt"),lines,Encoding.UTF8);
            return lines.Any(line=>line.StartsWith("FAIL")) ? 1 : 0;
        }
        public static void Core(Action<bool,string> check)
        {
            DateTimeOffset now=new DateTimeOffset(2026,10,7,8,0,0,TimeSpan.Zero);
            string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"TestArtifacts","learning-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,"words.json");
            File.WriteAllText(path,"[{\"Word\":\"curiosity\",\"Meaning\":\"好奇心\",\"Context\":\"Read with curiosity.\",\"SavedAt\":\"2026-10-04\"},{\"Word\":\"context\",\"Meaning\":\"语境\",\"Context\":\"Consider the original passage.\"}]");
            string original=File.ReadAllText(path); List<SavedWord> words=Storage.LoadWords(path); Vocabulary.EnsureIds(words);
            check(words.Count==2 && words.Select(word=>word.Id).Distinct().Count()==2 && File.ReadAllText(path)==original,"legacy vocabulary loads with stable in-memory IDs without changing the original file");
            string id=words[0].Id; Vocabulary.EnsureIds(words); check(words[0].Id==id,"existing IDs survive repeated initialization");
            check(Vocabulary.Due(words[0],now),"legacy and new words are immediately available for review");
            check(Vocabulary.Find(words,"CURI",0,now).Single().Word=="curiosity","word search ignores case");
            check(Vocabulary.Find(words,"好奇",0,now).Single().Word=="curiosity","search matches Chinese meanings");
            check(Vocabulary.Find(words,"passage",0,now).Single().Word=="context","search matches original context");
            check(!Vocabulary.Find(words,"unknown",0,now).Any(),"unmatched queries return no unrelated words");
            List<SavedWord> copied=Vocabulary.Copy(words); copied[0].Meaning="新释义"; check(words[0].Meaning=="好奇心","edits work on a deep copy before persistence");
            SavedWord target=words[0]; Vocabulary.Grade(target,Recall.Remembered,now);
            check(target.ReviewCount==1 && target.ReviewStage==1 && DateTimeOffset.Parse(target.NextReviewAt)==now.AddDays(1),"remembered new words are due in one day");
            check(!Vocabulary.Due(target,now.AddHours(23)) && Vocabulary.Due(target,now.AddDays(1)),"review becomes due at the scheduled instant");
            Vocabulary.Grade(target,Recall.Remembered,now.AddDays(1)); check(DateTimeOffset.Parse(target.NextReviewAt)==now.AddDays(4),"the next successful review uses a three-day interval");
            Vocabulary.Grade(target,Recall.Easy,now.AddDays(4)); check(target.ReviewStage==4 && DateTimeOffset.Parse(target.NextReviewAt)==now.AddDays(18),"easy words advance two stages");
            Vocabulary.Grade(target,Recall.Forgot,now); check(target.ReviewStage==0 && target.Lapses==1 && DateTimeOffset.Parse(target.NextReviewAt)==now.AddMinutes(10),"forgotten words return in ten minutes and reset the interval");
            target.Archived=true; check(!Vocabulary.Due(target,now.AddYears(1)) && Vocabulary.Find(words,"",2,now).Single()==target,"archived words are excluded from review and remain manageable");
            target.Archived=false; check(Vocabulary.Due(target,now.AddMinutes(10)),"restored words retain their prior review schedule");
            target.ReviewStage=100; Vocabulary.Grade(target,Recall.Easy,now); check(target.ReviewStage==6 && DateTimeOffset.Parse(target.NextReviewAt)==now.AddDays(60),"review intervals are bounded even with invalid old stages");
            Storage.WriteJson(path,words); List<SavedWord> loaded=Storage.LoadWords(path);
            check(loaded[0].Id==id && loaded[0].NextReviewAt==target.NextReviewAt && loaded[0].ReviewCount==target.ReviewCount && File.Exists(path+".bak"),"review progress persists alongside a previous-data backup");
            var late=new SavedWord { NextReviewAt=now.AddHours(8).ToOffset(TimeSpan.FromHours(8)).ToString("o") };
            check(!Vocabulary.Due(late,now.AddHours(7)) && Vocabulary.Due(late,now.AddHours(8)),"review due times handle timezone offsets correctly");
        }
        private static void Views(Action<bool,string> check)
        {
            var words=new List<SavedWord> { new SavedWord { Word="curiosity",Meaning="好奇心",Context="A curiosity." },new SavedWord { Word="context",Meaning="上下文",Context="Read the passage." } };
            using(MainForm main=new MainForm(true,words)) {
                main.CreateControl(); main.PerformLayout();
                TextBox query=(TextBox)main.Controls.Find("vocabularySearch",true).Single();
                ListView list=(ListView)main.Controls.Find("savedList",true).Single();
                query.Text="context";
                check(list.Items.Count==1 && ((SavedWord)list.Items[0].Tag).Word=="context","filtered rows retain their actual word identity instead of an unfiltered index");
                SavedWord selected=(SavedWord)list.Items[0].Tag;
                check(main.GradeWord(selected.Id,Recall.Remembered)=="" && words[0].ReviewCount==0,"grading a filtered word updates only the selected identity");
                query.Clear(); ((ComboBox)main.Controls.Find("vocabularyFilter",true).Single()).SelectedIndex=1;
                check(list.Items.Count==1 && ((SavedWord)list.Items[0].Tag).Word=="curiosity","due filter excludes words already reviewed today");
            }
            Vocabulary.EnsureIds(words);
            using(ReviewForm review=new ReviewForm(words,(id,grade)=>"",word=>{})) {
                SelfTest.Prepare(review);
                review.StartPosition=FormStartPosition.Manual; review.Location=new Point(-32000,-32000); review.ShowInTaskbar=false; review.Show(); Application.DoEvents();
                check(((TextBox)review.Controls.Find("reviewAnswer",true).Single()).TextLength==0 && !review.Controls.Find("gradeRemembered",true).Single().Enabled,"review hides answers and prevents grading before recall");
                ((Button)review.Controls.Find("revealAnswer",true).Single()).PerformClick();
                check(((TextBox)review.Controls.Find("reviewAnswer",true).Single()).Text.Contains("好奇心") && review.Controls.Find("gradeRemembered",true).Single().Enabled,"showing the answer reveals context and enables recall grading");
                using(Bitmap image=new Bitmap(review.Width,review.Height)) { review.DrawToBitmap(image,new Rectangle(Point.Empty,review.Size)); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"review-preview.png")); }
                ((Button)review.Controls.Find("gradeRemembered",true).Single()).PerformClick();
                check(((TextBox)review.Controls.Find("reviewAnswer",true).Single()).TextLength==0,"next word hides the prior answer again"); review.Close();
            }
            using(TranslationSettingsForm settings=new TranslationSettingsForm(new Settings(),"")) {
                SelfTest.Prepare(settings);
                settings.StartPosition=FormStartPosition.Manual; settings.Location=new Point(-32000,-32000); settings.ShowInTaskbar=false; settings.Show(); Application.DoEvents();
                check(((TextBox)settings.Controls.Find("deepseekApiKey",true).Single()).UseSystemPasswordChar,"the API-key field masks its value");
                check(settings.Provider=="free" && settings.Model=="deepseek-flash","translation settings display the default free mode and speed-oriented model");
                using(Bitmap image=new Bitmap(settings.Width,settings.Height)) { settings.DrawToBitmap(image,new Rectangle(Point.Empty,settings.Size)); image.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"translation-settings-preview.png")); }
                settings.Close();
            }
        }
    }
}
