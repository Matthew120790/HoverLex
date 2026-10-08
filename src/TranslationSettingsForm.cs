using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace HoverLex
{
    public sealed class TranslationSettingsForm : Form
    {
        private ComboBox provider, model;
        private TextBox key;
        private Label note;
        private CancellationTokenSource testing;
        public string Provider { get { return provider.SelectedIndex==1 ? "deepseek" : "free"; } }
        public string Model { get { return Convert.ToString(model.SelectedItem); } }
        public string ApiKey { get { return key.Text.Trim(); } }
        public TranslationSettingsForm(Settings settings,string savedKey)
        {
            Text="翻译设置"; StartPosition=FormStartPosition.CenterParent; Font=Theme.Font(10); BackColor=Theme.Background; Icon=Theme.AppIcon();
            AutoScaleMode=AutoScaleMode.Dpi; AutoScaleDimensions=new SizeF(96,96); ClientSize=new Size(590,430); MinimumSize=new Size(560,420);
            TableLayoutPanel layout=new TableLayoutPanel { Dock=DockStyle.Fill,Padding=new Padding(24),ColumnCount=1,RowCount=9 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(int height in new[] { 28,36,28,36,28,36,66 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,46));
            provider=new ComboBox { Name="translationProvider",Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList };
            provider.Items.AddRange(new object[] { "免费 · MyMemory 翻译 + 本机英文纠错", "DeepSeek · 翻译与纠错（按用量收费）" }); provider.SelectedIndex=settings.TranslationProvider=="deepseek" ? 1 : 0;
            model=new ComboBox { Name="deepseekModel",Dock=DockStyle.Fill,DropDownStyle=ComboBoxStyle.DropDownList };
            model.Items.AddRange(new object[] { "deepseek-flash", "deepseek-v4-pro" }); model.SelectedIndex=settings.DeepSeekModel=="deepseek-v4-pro" ? 1 : 0;
            key=new TextBox { Name="deepseekApiKey",Dock=DockStyle.Fill,UseSystemPasswordChar=true,MaxLength=512,Text=savedKey };
            Label privacy=Theme.Label("同样用于鼠标取词、手动查词与划词翻译。\n免费取词查本机词典；DeepSeek 取词发送单词和附近原文。\n密钥由当前 Windows 用户加密保存在本机；DeepSeek 按用量收费。",9,Theme.Muted); privacy.Dock=DockStyle.Fill;
            note=Theme.Label("Flash 优先速度；Pro 可选。均关闭思考模式。",9,Theme.Green); note.Dock=DockStyle.Fill; note.AutoSize=false;
            FlowLayoutPanel actions=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,FlowDirection=FlowDirection.RightToLeft };
            Button save=Theme.Button("保存",true); save.AutoSize=true; save.Click+=delegate { if(Provider=="deepseek" && ApiKey.Length==0) { note.Text="请先输入 API 密钥。"; return; } DialogResult=DialogResult.OK; };
            Button cancel=Theme.Button("取消",false); cancel.AutoSize=true; cancel.DialogResult=DialogResult.Cancel; CancelButton=cancel;
            Button test=Theme.Button("测试连接",false); test.AutoSize=true;
            test.Click+=async delegate {
                if(testing!=null) return;
                testing=new CancellationTokenSource(); CancellationTokenSource owned=testing; test.Enabled=false; save.Enabled=false;
                string selected=Provider; note.Text=selected=="deepseek" ? "正在测试 DeepSeek（会产生少量用量）…" : "正在测试免费翻译…";
                try {
                    string result=selected=="deepseek" ? await DeepSeekTranslation.TranslateAsync("你好，世界！",TranslationDirection.ChineseToEnglish,true,new TranslationOptions { Provider="deepseek",Model=Model,Key=ApiKey },owned.Token) : await new OnlineTranslator().TranslateFreeForTestAsync("你好，世界！",owned.Token);
                    if(!IsDisposed && !owned.IsCancellationRequested) note.Text="连接成功："+result;
                } catch(OperationCanceledException) { }
                catch(Exception error) { if(!IsDisposed) note.Text=error.Message; }
                finally { if(!IsDisposed) { test.Enabled=true; save.Enabled=true; } if(testing==owned) testing=null; owned.Dispose(); }
            };
            provider.SelectedIndexChanged+=delegate { model.Enabled=key.Enabled=Provider=="deepseek"; test.Text=Provider=="deepseek" ? "测试（少量计费）" : "测试连接"; };
            model.Enabled=key.Enabled=Provider=="deepseek"; test.Text=Provider=="deepseek" ? "测试（少量计费）" : "测试连接";
            actions.Controls.AddRange(new Control[] { save,cancel,test });
            layout.Controls.Add(Theme.Label("翻译与纠错方式",10,Theme.Green),0,0); layout.Controls.Add(provider,0,1);
            layout.Controls.Add(Theme.Label("DeepSeek 模型",10,Theme.Green),0,2); layout.Controls.Add(model,0,3);
            layout.Controls.Add(Theme.Label("API 密钥（在 platform.deepseek.com/api_keys 创建）",9,Theme.Green),0,4); layout.Controls.Add(key,0,5);
            layout.Controls.Add(privacy,0,6); layout.Controls.Add(note,0,7); layout.Controls.Add(actions,0,8); Controls.Add(layout);
            AutoScaleDimensions=new SizeF(96,96); PerformAutoScale(); float scale=CurrentAutoScaleDimensions.Width/96f;
            ClientSize=new Size((int)(590*scale),(int)(430*scale)); MinimumSize=new Size((int)(560*scale),(int)(420*scale));
            FormClosing+=delegate { if(testing!=null) testing.Cancel(); };
        }
    }
}
