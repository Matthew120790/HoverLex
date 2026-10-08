using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HoverLex
{
    public sealed class ReviewForm : Form
    {
        private readonly List<SavedWord> queue;
        private readonly Func<string, Recall, string> save;
        private readonly Action<string> speak;
        private Label progress, word, hint, note;
        private TextBox answer;
        private Button reveal;
        private FlowLayoutPanel grades;
        private int index;
        public ReviewForm(IEnumerable<SavedWord> words, Func<string, Recall, string> saveGrade, Action<string> speakWord)
        {
            queue = words.ToList(); save = saveGrade; speak = speakWord;
            Text = "生词复习 · 先回想，再看答案"; StartPosition = FormStartPosition.CenterParent;
            Font = Theme.Font(10); BackColor = Theme.Background; Icon = Theme.AppIcon();
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96,96); ClientSize = new Size(580,470); MinimumSize = new Size(520,430);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach (int height in new[] { 32,64,30 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            foreach (int height in new[] { 42,46,36 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            progress = Theme.Label("",9,Theme.Muted); progress.Dock = DockStyle.Fill;
            word = Theme.Label("",25,Theme.Green); word.Font = Theme.Font(25,FontStyle.Bold); word.Dock = DockStyle.Fill;
            hint = Theme.Label("回想释义和用法，再显示答案。",9,Theme.Muted); hint.Dock = DockStyle.Fill;
            answer = new TextBox { Name="reviewAnswer",Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Color.White, Font = Theme.Font(12) };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            reveal = Theme.Button("显示答案",true); reveal.Name = "revealAnswer"; reveal.AutoSize = true; reveal.Click += delegate { answer.Text = (queue[index].Meaning ?? "") + "\r\n\r\n原文：" + (queue[index].Context ?? ""); grades.Enabled = true; reveal.Enabled = false; };
            Button voice = Theme.Button("发音",false); voice.AutoSize = true; voice.Click += delegate { if(index < queue.Count) speak(queue[index].Word); };
            actions.Controls.AddRange(new Control[] { reveal,voice });
            grades = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Enabled = false };
            AddGrade("忘记 · 10 分钟",Recall.Forgot); AddGrade("记得",Recall.Remembered); AddGrade("很熟",Recall.Easy);
            note = Theme.Label("",9,Theme.Muted); note.Dock = DockStyle.Fill; note.AutoSize = false; note.AutoEllipsis = true;
            layout.Controls.Add(progress,0,0); layout.Controls.Add(word,0,1); layout.Controls.Add(hint,0,2); layout.Controls.Add(answer,0,3); layout.Controls.Add(actions,0,4); layout.Controls.Add(grades,0,5); layout.Controls.Add(note,0,6);
            Controls.Add(layout); Next();
            AutoScaleDimensions=new SizeF(96,96); PerformAutoScale(); float scale=CurrentAutoScaleDimensions.Width/96f;
            ClientSize=new Size((int)(580*scale),(int)(470*scale)); MinimumSize=new Size((int)(520*scale),(int)(430*scale));
        }
        private void AddGrade(string title, Recall recall)
        {
            Button button = Theme.Button(title,false); button.Name="grade"+recall; button.AutoSize = true; button.Height = 36;
            button.Click += delegate {
                string error = save(queue[index].Id,recall);
                if (!String.IsNullOrEmpty(error)) { note.Text = error; return; }
                index++; Next();
            };
            grades.Controls.Add(button);
        }
        private void Next()
        {
            grades.Enabled = false; answer.Clear(); note.Text = "忘记的词稍后重练；记得的词按间隔安排下次复习。";
            if(index >= queue.Count) { progress.Text = "本轮完成 · " + queue.Count + " 个词"; word.Text = "今天的复习完成了"; word.Font = Theme.Font(18,FontStyle.Bold); hint.Text = "下次到期会出现在生词本中。"; reveal.Enabled = false; return; }
            progress.Text = "第 " + (index+1) + " / " + queue.Count + " 个 · 本轮最多 20 个";
            word.Text = queue[index].Word; reveal.Enabled = true;
            grades.Controls.Find("gradeRemembered",false)[0].Text="记得 · "+Vocabulary.NextIntervalDays(queue[index],Recall.Remembered)+" 天";
            grades.Controls.Find("gradeEasy",false)[0].Text="很熟 · "+Vocabulary.NextIntervalDays(queue[index],Recall.Easy)+" 天";
        }
    }
    public sealed class EditWordForm : Form
    {
        private TextBox meaning, context;
        public string Meaning { get { return meaning.Text.Trim(); } }
        public string ContextText { get { return context.Text; } }
        public EditWordForm(SavedWord word)
        {
            Text = "编辑生词 · " + word.Word; StartPosition = FormStartPosition.CenterParent; BackColor = Theme.Background; Font = Theme.Font(10); Icon = Theme.AppIcon();
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96,96); ClientSize = new Size(530,360); MinimumSize = new Size(450,330);
            TableLayoutPanel layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,45)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,55)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));
            meaning = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = word.Meaning, MaxLength = 10000 };
            context = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = word.Context, MaxLength = 20000 };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            Button save = Theme.Button("保存",true); save.AutoSize = true; save.Click += delegate { if(Meaning.Length == 0) { MessageBox.Show(this,"请输入释义。","编辑生词"); return; } DialogResult = DialogResult.OK; };
            Button cancel = Theme.Button("取消",false); cancel.AutoSize = true; cancel.DialogResult = DialogResult.Cancel; CancelButton = cancel;
            actions.Controls.AddRange(new Control[] { save,cancel });
            layout.Controls.Add(Theme.Label("释义",10,Theme.Green),0,0); layout.Controls.Add(meaning,0,1); layout.Controls.Add(Theme.Label("原文 / 笔记",10,Theme.Green),0,2); layout.Controls.Add(context,0,3); layout.Controls.Add(actions,0,4); Controls.Add(layout);
            AutoScaleDimensions=new SizeF(96,96); PerformAutoScale(); float scale=CurrentAutoScaleDimensions.Width/96f;
            ClientSize=new Size((int)(530*scale),(int)(360*scale)); MinimumSize=new Size((int)(450*scale),(int)(330*scale));
        }
    }
}
