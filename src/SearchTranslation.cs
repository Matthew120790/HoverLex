using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverLex
{
    public sealed class SearchTranslation : IDisposable
    {
        private readonly TextBox input;
        private readonly Action<string> status;
        private readonly Func<string,Entry> lookup;
        private readonly Action<string> showWord;
        private readonly OnlineTranslator service=new OnlineTranslator();
        private readonly Func<string,TranslationDirection,CancellationToken,Task<string>> translate;
        internal readonly TranslationPanel Panel;
        private CancellationTokenSource request;
        private InputSnapshot source;
        private int version;
        private bool disposed,replacing;
        public bool EnglishCorrection { get { return service.CorrectionEnabled; } set { if(service.CorrectionEnabled==value) return; Dismiss(); service.CorrectionEnabled=value; } }
        public SearchTranslation(TextBox editor,Action<string> message,Func<string,Entry> wordLookup,Action<string> displayWord,Func<string,TranslationDirection,CancellationToken,Task<string>> translator=null,IEnglishSpeech player=null)
        {
            input=editor; status=message; lookup=wordLookup; showWord=displayWord; translate=translator ?? service.TranslateAsync;
            Panel=new TranslationPanel(player); input.TextChanged+=Changed;
            Panel.Dismiss=Dismiss;
            Panel.Retry=async ()=>await QueryAsync();
            Panel.ReplaceText=(expected,text)=> {
                if(disposed || input.IsDisposed || expected!=source || input.Text!=expected.Text) return Task.FromResult("输入已变化，未替换");
                replacing=true;
                try { input.Text=text; input.SelectionStart=input.TextLength; source=null; return Task.FromResult(""); }
                finally { replacing=false; }
            };
        }
        private void Changed(object sender,EventArgs e) { if(!replacing) Dismiss(); }
        private void Cancel() { if(request!=null) { request.Cancel(); request=null; } }
        public void Dismiss() { version++; Cancel(); source=null; Panel.Hide(); }
        public bool HandleTab()
        {
            if(disposed || !input.Focused || source==null || input.Text!=source.Text || !Panel.CanReplace || !Panel.IsShown) return false;
            ApplyTab(); return true;
        }
        private async void ApplyTab() { await Panel.ApplyReplacementAsync(); }
        public async Task QueryAsync()
        {
            if(disposed) return;
            Dismiss(); string text=input.Text;
            if(String.IsNullOrWhiteSpace(text) || text.Length>2000) { status("请输入不超过 2000 字的中文或英文"); return; }
            TranslationDirection direction=OnlineTranslator.DirectionFor(text);
            string word=Words.Clean(text);
            if(direction==TranslationDirection.EnglishToChinese && word.Length>0 && lookup!=null) {
                Entry entry=lookup(word);
                if(entry!=null && !String.IsNullOrWhiteSpace(entry.Chinese)) { showWord(word); return; }
            }
            Form owner=input.FindForm(); if(owner!=null && owner.ContainsFocus) input.Focus();
            Point location=input.PointToScreen(Point.Empty);
            source=new InputSnapshot { Id="manual-search",Text=text,Mode="manual-search",Editable=true,FocusHandle=input.Handle.ToInt64(),X=location.X,Y=location.Y,Width=input.Width,Height=input.Height };
            InputSnapshot expected=source; int generation=version;
            CancellationTokenSource owned=new CancellationTokenSource(); request=owned;
            Panel.Pending(expected,direction==TranslationDirection.EnglishToChinese,false,EnglishCorrection); Panel.ShowNear(expected); status("正在翻译…");
            try {
                string result=await translate(text,direction,owned.Token);
                if(disposed || owned.IsCancellationRequested || generation!=version || input.Text!=text) return;
                Panel.Result(expected,result,direction==TranslationDirection.EnglishToChinese); status("译文已显示 · Tab 替换，也可复制或朗读英文");
            } catch(OperationCanceledException) { }
            catch(Exception error) { if(!disposed && !owned.IsCancellationRequested && generation==version) { Panel.Failure(error.Message); status(error.Message); } }
            finally { if(request==owned) request=null; owned.Dispose(); }
        }
        public void Dispose() { if(disposed) return; disposed=true; input.TextChanged-=Changed; Dismiss(); Panel.Dispose(); service.ClearCache(); }
    }
}
