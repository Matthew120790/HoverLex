using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverLex
{
    public sealed partial class MainForm
    {
        private TextBox wordSearch;
        private ComboBox wordFilter;
        private Button reviewButton, archiveButton, undoWords;
        private Label onlineNote;
        private List<SavedWord> previousWords;
        private bool showingHelp;
        private readonly System.Windows.Forms.Timer reviewTimer=new System.Windows.Forms.Timer();
        private Control BuildLearningTools()
        {
            TableLayoutPanel layout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,36)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,38));
            TableLayoutPanel row=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=3,RowCount=1,Margin=new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,108)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,126)); row.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            wordSearch=new TextBox { Name="vocabularySearch",Dock=DockStyle.Fill,Font=Theme.Font(10),MaxLength=500,AccessibleName="搜索生词、释义和原文" };
            wordSearch.HandleCreated+=delegate { Native.SendMessage(wordSearch.Handle,0x1501,new IntPtr(1),"搜索单词、释义、原文…"); };
            wordSearch.TextChanged+=delegate { if(savedList!=null) RefreshWords(); };
            wordFilter=new ComboBox { Name="vocabularyFilter",Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList,Font=Theme.Font(9) }; wordFilter.Items.AddRange(new object[] { "学习中", "待复习", "已归档" }); wordFilter.SelectedIndex=0;
            wordFilter.SelectedIndexChanged+=delegate { archiveButton.Text=wordFilter.SelectedIndex==2 ? "恢复学习" : "归档"; if(savedList!=null) RefreshWords(); };
            reviewButton=Theme.Button("开始复习",true); reviewButton.Name="startReview"; reviewButton.Dock=DockStyle.Fill; reviewButton.Margin=new Padding(8,0,0,3); reviewButton.Font=Theme.Font(9); reviewButton.Click+=delegate { StartReview(); };
            row.Controls.Add(wordSearch,0,0); row.Controls.Add(wordFilter,1,0); row.Controls.Add(reviewButton,2,0);
            FlowLayoutPanel actions=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,Margin=new Padding(0,3,0,0) };
            Button edit=LearningButton("编辑",delegate { EditSelectedWord(); }); edit.Name="editWord";
            archiveButton=LearningButton("归档",delegate { ArchiveSelectedWords(); }); archiveButton.Name="archiveWords";
            Button delete=LearningButton("删除",delegate { DeleteSelectedWords(); }); delete.Name="deleteWords";
            undoWords=LearningButton("撤销",delegate { UndoWordChange(); }); undoWords.Name="undoWords"; undoWords.Enabled=false;
            CheckBox reminder=new SwitchBox { Name="reviewReminder",Text="每日复习提醒",AutoSize=true,Font=Theme.Font(9),ForeColor=Theme.Green,Checked=settings.ReviewReminder,Margin=new Padding(12,2,0,0) };
            reminder.CheckedChanged+=delegate { settings.ReviewReminder=reminder.Checked; if(previewMode) return; try { settings.Save(Path.Combine(userDir,"settings.json")); } catch(Exception error) { status.Text="提醒设置保存失败："+error.Message; } };
            actions.Controls.AddRange(new Control[] { edit,archiveButton,delete,undoWords,reminder });
            layout.Controls.Add(row,0,0); layout.Controls.Add(actions,0,1); return layout;
        }
        private Button LearningButton(string title,Action click)
        {
            Button button=Theme.Button(title,false); button.Font=Theme.Font(9); button.Size=new Size(58,30); button.Margin=new Padding(0,0,6,0); button.Click+=delegate { click(); }; return button;
        }
        private HashSet<string> SelectedWordIds()
        {
            return new HashSet<string>(savedList.SelectedItems.Cast<ListViewItem>().Select(item=>((SavedWord)item.Tag).Id));
        }
        internal bool CommitWords(List<SavedWord> updated,bool remember=true)
        {
            try {
                if(!previewMode) Storage.WriteJson(Path.Combine(userDir,"words.json"),updated);
                if(remember) previousWords=Vocabulary.Copy(saved);
                saved=updated; RefreshWords(); undoWords.Enabled=previousWords!=null; return true;
            } catch(Exception error) { status.Text="生词保存失败，原数据已保留："+error.Message; return false; }
        }
        private void EditSelectedWord()
        {
            HashSet<string> ids=SelectedWordIds(); if(ids.Count!=1) { status.Text="请选中一个生词再编辑"; return; }
            string id=ids.First(); SavedWord selected=saved.First(word=>word.Id==id);
            using(EditWordForm dialog=new EditWordForm(selected)) if(ShowInternalDialog(dialog)==DialogResult.OK) {
                List<SavedWord> updated=Vocabulary.Copy(saved); SavedWord word=updated.First(item=>item.Id==id); word.Meaning=dialog.Meaning; word.Context=dialog.ContextText;
                if(CommitWords(updated)) status.Text="生词已更新 · 可撤销";
            }
        }
        private void ArchiveSelectedWords()
        {
            HashSet<string> ids=SelectedWordIds(); if(ids.Count==0) { status.Text="请先选择生词（可按 Ctrl 多选）"; return; }
            bool archive=wordFilter.SelectedIndex!=2; List<SavedWord> updated=Vocabulary.Copy(saved);
            foreach(SavedWord word in updated.Where(word=>ids.Contains(word.Id))) word.Archived=archive;
            if(CommitWords(updated)) status.Text=archive ? "已归档 · 在已归档筛选中可恢复" : "已恢复学习";
        }
        private void DeleteSelectedWords()
        {
            HashSet<string> ids=SelectedWordIds(); if(ids.Count==0) { status.Text="请先选择要删除的生词"; return; }
            if(MessageBox.Show(this,"删除选中的 "+ids.Count+" 个生词？\n也可以先归档，保留记录。","确认删除",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes) return;
            if(CommitWords(Vocabulary.Copy(saved.Where(word=>!ids.Contains(word.Id))))) status.Text="已删除 · 可点击撤销恢复";
        }
        private void UndoWordChange()
        {
            if(previousWords==null) return; List<SavedWord> restore=Vocabulary.Copy(previousWords);
            if(CommitWords(restore,false)) { previousWords=null; undoWords.Enabled=false; status.Text="已撤销上次生词修改"; }
        }
        private void RefreshReviewCount()
        {
            if(savedCount==null || wordSearch==null) return;
            int due=saved.Count(word=>Vocabulary.Due(word,DateTimeOffset.UtcNow));
            if(!showingHelp) {
                savedCount.Text="生词本 · "+saved.Count(word=>!word.Archived)+" 个 · 待复习 "+due;
                if(wordSearch.TextLength>0) savedCount.Text+=" · 找到 "+savedList.Items.Count;
            }
            reviewButton.Text="复习（"+due+"）"; reviewButton.Enabled=due>0;
        }
        private void StartReview()
        {
            var due=saved.Where(word=>Vocabulary.Due(word,DateTimeOffset.UtcNow)).OrderBy(word=>word.NextReviewAt ?? "").Take(20).ToList();
            if(due.Count==0) { status.Text="本轮没有到期生词，明天再来看看"; return; }
            using(ReviewForm dialog=new ReviewForm(due,GradeWord,Speak)) ShowInternalDialog(dialog);
            RefreshWords();
        }
        internal string GradeWord(string id,Recall recall)
        {
            List<SavedWord> updated=Vocabulary.Copy(saved); SavedWord word=updated.FirstOrDefault(item=>item.Id==id);
            if(word==null) return "这个生词已不存在";
            Vocabulary.Grade(word,recall,DateTimeOffset.UtcNow);
            return CommitWords(updated) ? "" : "保存失败，请重试；本次不会跳到下一个词";
        }
        private void CheckReviewReminder()
        {
            if(previewMode || tray==null || !settings.ReviewReminder) return;
            string today=DateTime.Now.ToString("yyyy-MM-dd"); if(settings.LastReviewReminder==today) return;
            int due=saved.Count(word=>Vocabulary.Due(word,DateTimeOffset.UtcNow)); if(due==0) return;
            try {
                settings.LastReviewReminder=today; settings.Save(Path.Combine(userDir,"settings.json"));
                tray.ShowBalloonTip(5000,"HoverLex · 今日复习",due+" 个生词待复习，点击开始。",ToolTipIcon.Info);
            } catch(Exception error) { status.Text="复习提醒未保存："+error.Message; }
        }
        private void ConfigureTranslation()
        {
            using(TranslationSettingsForm dialog=new TranslationSettingsForm(settings,previewMode ? "" : ApiKeyStore.Load(userDir))) {
                if(ShowInternalDialog(dialog)!=DialogResult.OK) return;
                try {
                    if(!previewMode) ApiKeyStore.Save(userDir,dialog.ApiKey);
                    string oldProvider=settings.TranslationProvider,oldModel=settings.DeepSeekModel;
                    settings.TranslationProvider=dialog.Provider; settings.DeepSeekModel=dialog.Model;
                    try { if(!previewMode) settings.Save(Path.Combine(userDir,"settings.json")); }
                    catch { settings.TranslationProvider=oldProvider; settings.DeepSeekModel=oldModel; throw; }
                    if(searchTranslation!=null) searchTranslation.Dismiss();
                    if(translation!=null) translation.Dismiss();
                    if(selectionTranslation!=null) selectionTranslation.Dismiss();
                    DismissHoverLookup(); hoverTranslation.Clear();
                    TranslationPreferences.Configure(dialog.Provider,dialog.Model,dialog.ApiKey);
                    onlineNote.Text=dialog.Provider=="deepseek" ? "DeepSeek 翻译与纠错" : "MyMemory 翻译 · 本机纠错";
                    status.Text=dialog.Provider=="deepseek" ? "已切换 DeepSeek · 文字会联网处理，按用量收费" : "已切换免费模式 · 英文纠错在本机";
                    if(dialog.Provider=="free" && settings.EnglishCorrection && !previewMode) WarmProofreader();
                } catch(Exception error) { status.Text="翻译设置保存失败："+error.Message; }
            }
        }
        private async void WarmProofreader() { await EnglishProofreader.WarmupAsync(); }
        private DialogResult ShowInternalDialog(Form dialog)
        {
            bool wasSuspended=translation!=null && translation.Suspended;
            if(translation!=null) { translation.Dismiss(); translation.Suspended=true; }
            if(selectionTranslation!=null) selectionTranslation.Dismiss();
            DismissHoverLookup();
            try { return dialog.ShowDialog(this); }
            finally { if(translation!=null) translation.Suspended=wasSuspended; }
        }
    }
}
