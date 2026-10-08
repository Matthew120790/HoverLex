using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HoverLex
{
    public sealed class TranslationPanel : Form
    {
        private readonly Label original, note, title;
        private readonly Label english;
        private readonly Button copy, replace, retry, speak;
        private readonly TableLayoutPanel actions;
        private readonly IEnglishSpeech speech;
        private readonly bool ownsSpeech;
        private bool ready;
        private bool applying;
        private int contentVersion;
        private bool toChinese;
        private string speechText="";
        private InputSnapshot source;
        public Func<InputSnapshot,string,Task<string>> ReplaceText;
        public Action Retry;
        public Action Dismiss;
        public string English { get { return english.Text; } }
        public bool CanReplace { get { return ready && !applying && replace.Enabled && source!=null && ReplaceText!=null; } }
        public bool IsShown { get { return IsHandleCreated && IsWindowVisible(Handle); } }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { CreateParams p=base.CreateParams; p.ExStyle|=0x08000088; p.ClassStyle|=0x20000; return p; } }
        public TranslationPanel(IEnglishSpeech player=null)
        {
            ownsSpeech=player==null; speech=player ?? new EnglishSpeech(); speech.Changed+=SpeechChanged;
            AutoScaleMode=AutoScaleMode.Dpi; AutoScaleDimensions=new SizeF(96,96); ClientSize=new Size(440,290);
            FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; BackColor=Color.White; Padding=new Padding(20); Font=Theme.Font(9);
            TableLayoutPanel layout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=5,Margin=new Padding(0) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            foreach(float height in new float[] { 30,44 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute,height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent,100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute,42));
            title=Theme.Label("中文 → 英文",14,Theme.Ink); title.Font=Theme.Font(14,FontStyle.Bold);
            original=Theme.Label("",9,Theme.Muted); original.AutoSize=false; original.AutoEllipsis=true;
            english=new Label { AutoSize=true,BackColor=Color.White,ForeColor=Theme.Ink,Font=new Font("Segoe UI",11),AccessibleName="英文译文",Dock=DockStyle.Top,Margin=new Padding(0) };
            Panel translationBody=new Panel { Dock=DockStyle.Fill,AutoScroll=true,BackColor=Color.White,Margin=new Padding(0) }; translationBody.Controls.Add(english);
            translationBody.Resize+=delegate { english.MaximumSize=new Size(Math.Max(1,translationBody.ClientSize.Width-SystemInformation.VerticalScrollBarWidth),0); };
            note=Theme.Label("",8,Theme.Muted); note.AutoSize=false; note.AutoEllipsis=true;
            actions=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=5,RowCount=1,Margin=new Padding(0) };
            actions.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            for(int i=0;i<5;i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,20));
            copy=Theme.Button("复制",true); replace=Theme.Button("替换",false); retry=Theme.Button("重译",false); Button close=Theme.Button("关闭",false);
            speak=Theme.Button("朗读",false); speak.Name="speakTranslation"; speak.AccessibleName="朗读英文译文"; speak.Enabled=false;
            Button[] buttons={ copy,replace,speak,retry,close };
            for(int i=0;i<buttons.Length;i++) { ((SoftButton)buttons[i]).PreserveInputFocus=true; buttons[i].TabStop=false; buttons[i].Dock=DockStyle.Fill; buttons[i].Margin=new Padding(0,0,i==buttons.Length-1 ? 0 : 8,0); actions.Controls.Add(buttons[i],i,0); }
            Control[] rows={ title,original,translationBody,note,actions };
            for(int i=0;i<rows.Length;i++) { rows[i].Dock=DockStyle.Fill; layout.Controls.Add(rows[i],0,i); }
            Controls.Add(layout); AutoScaleDimensions=new SizeF(96,96); PerformAutoScale();
            close.Click+=delegate { if(Dismiss!=null) Dismiss(); else Hide(); };
            speak.Click+=delegate { if(!ready) return; if(speech.IsSpeaking) { speech.Stop(); note.Text="已停止朗读"; } else { string error=speech.Play(speechText); if(error.Length>0) note.Text=error; } };
            retry.Click+=delegate { if(Retry!=null) Retry(); };
            copy.Click+=delegate { try { Clipboard.SetText(english.Text); note.Text="已复制译文"; } catch { note.Text="剪贴板暂时忙，请再点一次复制"; } };
            replace.Click+=async delegate { await ApplyReplacementAsync(); };
        }
        public async Task ApplyReplacementAsync()
        {
            if(!CanReplace) return;
            int version=contentVersion; applying=true;
            copy.Enabled=replace.Enabled=retry.Enabled=false;
            bool replaced=false;
            try { string error=await ReplaceText(source,english.Text); if(IsDisposed || version!=contentVersion) return; if(error.Length==0) { replaced=true; Replaced(false); } else note.Text=error; }
            catch { if(!IsDisposed && version==contentVersion) note.Text="替换未完成，请使用复制"; }
            finally { applying=false; if(!IsDisposed && version==contentVersion && !replaced) copy.Enabled=replace.Enabled=retry.Enabled=true; }
        }
        private void Direction(bool chineseResult) { toChinese=chineseResult; title.Text=toChinese ? "英文 → 中文" : "中文 → 英文"; english.AccessibleName=toChinese ? "中文译文" : "英文译文"; speak.AccessibleName=toChinese ? "朗读英文原文" : "朗读英文译文"; }
        public void Pending(InputSnapshot input,bool chineseResult=false,bool correctionOnly=false,bool englishCorrection=true)
        {
            contentVersion++;
            speech.Stop(); ready=false; speak.Enabled=false;
            Direction(chineseResult);
            retry.Text=correctionOnly ? "重试" : "重译";
            if(correctionOnly) { title.Text="英文纠错"; english.AccessibleName="纠正后的英文"; }
            source=input; original.Text=input.Text.Replace("\r"," ").Replace("\n"," "); english.Text="正在翻译…";
            if(correctionOnly) english.Text="正在检查英文…";
            note.Text=(englishCorrection ? "先纠正英文" : "在线翻译")+" · 可继续输入，旧结果会自动取消"; copy.Enabled=replace.Enabled=retry.Enabled=false;
        }
        public void Result(InputSnapshot input,string translation,bool chineseResult=false,bool correctionOnly=false)
        {
            contentVersion++;
            speech.Stop(); Direction(chineseResult); ready=true; speechText=toChinese ? input.Text : translation; speak.Enabled=!String.IsNullOrWhiteSpace(speechText);
            retry.Text=correctionOnly ? "重试" : "重译";
            source=input; original.Text=input.Text.Replace("\r"," ").Replace("\n"," "); english.Text=translation;
            if(correctionOnly) { title.Text="英文纠错"; english.AccessibleName="纠正后的英文"; }
            note.Text=toChinese ? "Tab 替换 · 朗读英文原文" : "Tab 替换 · 点击替换前会再次核对原文"; copy.Enabled=retry.Enabled=true; replace.Enabled=ReplaceText!=null;
            if(correctionOnly) note.Text="已纠正英文 · Tab 替换，也可复制或朗读";
        }
        public void PendingSelection(InputSnapshot input)
        {
            bool chineseResult=OnlineTranslator.DirectionFor(input.Text)==TranslationDirection.EnglishToChinese;
            Pending(input,chineseResult); title.Text=chineseResult ? "选中英文 → 中文" : "选中中文 → 英文"; note.Text="正在翻译选中文字…"; SelectionActions();
        }
        public void SelectionResult(InputSnapshot input,string translation)
        {
            bool chineseResult=OnlineTranslator.DirectionFor(input.Text)==TranslationDirection.EnglishToChinese;
            Result(input,translation,chineseResult); title.Text=chineseResult ? "选中英文 → 中文" : "选中中文 → 英文"; note.Text=chineseResult ? "复制中文译文 · 朗读英文原文" : "复制英文译文 · 朗读英文译文"; SelectionActions();
        }
        private void SelectionActions() { replace.Visible=false; for(int i=0;i<5;i++) actions.ColumnStyles[i].Width=i==1 ? 0 : 25; }
        public void Failure(string error) { contentVersion++; speech.Stop(); ready=false; speak.Enabled=false; english.Text=error; note.Text="原输入已保留 · 点击“"+retry.Text+"”再试"; copy.Enabled=replace.Enabled=false; retry.Enabled=true; }
        public void Replaced(bool automatic) { replace.Enabled=retry.Enabled=false; copy.Enabled=speak.Enabled=ready; note.Text=(automatic ? "已自动替换" : "已替换")+" · 点击朗读英文"; }
        private void SpeechChanged()
        {
            if(IsDisposed || Disposing) return;
            if(InvokeRequired) { try { BeginInvoke(new Action(SpeechChanged)); } catch(InvalidOperationException) { } return; }
            speak.Text=speech.IsSpeaking ? "停止" : "朗读";
            if(ready) note.Text=speech.Error.Length>0 ? speech.Error : speech.IsSpeaking ? "正在朗读英文…" : "朗读已结束 · 可再次播放";
        }
        public void ReplacementFailed(string message) { note.Text=message; }
        public void ShowNear(InputSnapshot input)
        {
            Point anchor=new Point(input.X,input.Y); Rectangle area=Screen.FromPoint(anchor).WorkingArea;
            int x=input.X+input.Width+12; if(x+Width>area.Right) x=input.X+12;
            int y=input.Y+20; if(input.Height<100) y=input.Y+input.Height+12;
            Location=new Point(Math.Max(area.Left,Math.Min(x,area.Right-Width)),Math.Max(area.Top,Math.Min(y,area.Bottom-Height)));
            IntPtr window=Handle; Prepare(this);
            // 直接无激活显示，避免 Form.Show 在选择首个控件时切走输入焦点。
            Native.SetWindowPos(window,new IntPtr(-1),0,0,0,0,0x0053);
        }
        private static void Prepare(Control control) { foreach(Control child in control.Controls) { IntPtr handle=child.Handle; Prepare(child); } control.PerformLayout(); }
        public new void Hide() { contentVersion++; ready=false; source=null; speech.Stop(); if(IsHandleCreated) Native.SetWindowPos(Handle,IntPtr.Zero,0,0,0,0,0x0097); base.Hide(); }
        protected override void Dispose(bool disposing) { if(disposing) { speech.Changed-=SpeechChanged; speech.Stop(); if(ownsSpeech) speech.Dispose(); } base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); if(Width<=1 || Height<=1) return;
            e.Graphics.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using(var path=Theme.Round(new RectangleF(0,0,Width-1,Height-1),12*e.Graphics.DpiX/96f))
            using(Pen border=new Pen(Theme.Line)) e.Graphics.DrawPath(border,path);
        }
    }

    public sealed class TranslationController : IDisposable
    {
        private readonly TranslationPanel panel=new TranslationPanel();
        private readonly InputFeedback feedback=new InputFeedback();
        private readonly InputRecovery recovery=new InputRecovery();
        private readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private readonly InputTranslationGate gate=new InputTranslationGate();
        private readonly OnlineTranslator service=new OnlineTranslator();
        private readonly Action<string> status;
        private InputMonitor monitor;
        private InputActivity activity;
        private CancellationTokenSource request;
        private InputSnapshot shown;
        private bool enabled,disposed,autoReplace,replacing,receipt;
        internal TranslationPanel Panel { get { return panel; } }
        internal InputFeedback Feedback { get { return feedback; } }
        private long revision,requestRevision,serial;
        private long receiptFocus;
        public long ExcludedFocusHandle;
        public Func<long,bool> ExcludeFocus;
        public bool Suspended;
        internal long AllowedWindow;
        private readonly Func<string,CancellationToken,Task<string>> translate;
        private long requestAt;
        private long lastErrorSerial;
        private bool readerBlocked;
        private long blockedFocus;
        private long blockedRevision;
        public bool Enabled { get { return enabled; } }
        public bool AutoReplace { get { return autoReplace; } set { if(autoReplace==value) return; autoReplace=value; Dismiss(); } }
        public bool EnglishCorrection {
            get { return service.CorrectionEnabled; }
            set { if(service.CorrectionEnabled==value) return; Dismiss(); service.CorrectionEnabled=value; if(enabled) status(value ? "已开启 · 中文转英文，英文自动纠错" : "已开启 · 中文转英文，英文纠错已关闭"); }
        }
        public TranslationController(Action<string> message,Func<string,CancellationToken,Task<string>> translator=null)
        {
            translate=translator ?? service.TranslateAsync;
            status=message; timer.Interval=100; timer.Tick+=Tick;
            panel.Dismiss=Dismiss;
            panel.ReplaceText=async (source,english)=> {
                if(!enabled) return "输入助手已关闭";
                if(replacing || activity.Revision!=requestRevision || monitor==null || !monitor.Fresh || !InputReader.SameSource(source,monitor.Latest)) return "输入或光标已变化，请重新检查";
                Cancel();
                var owned=new CancellationTokenSource(); request=owned;
                source.RequirePointer=false;
                replacing=true;
                try { string error=await InputMonitor.ReplaceAsync(source,english,owned.Token); if(error.Length==0 && enabled && !disposed && !owned.IsCancellationRequested) { gate.Reset(activity.Serial); shown=null; receipt=true; receiptFocus=source.FocusHandle; status("已替换 · 未发送，可继续输入"); } return error; }
                finally { replacing=false; if(request==owned) request=null; owned.Dispose(); }
            };
            panel.Retry=()=> { if(enabled && !replacing && shown!=null && monitor!=null && monitor.Fresh && activity.Revision==requestRevision && InputReader.SameSource(shown,monitor.Latest)) Translate(shown,gate.Generation); else { Dismiss(); status("原文或光标已变化，请继续输入后重新检查"); } };
        }
        public bool SetEnabled(bool value)
        {
            timer.Stop(); enabled=false; receipt=false; Cancel(); gate.Reset(); panel.Hide(); feedback.Hide(); shown=null;
            if(monitor!=null) { monitor.Dispose(); monitor=null; } if(activity!=null) { activity.Dispose(); activity=null; }
            service.ClearCache();
            if(!value) { status("输入助手已关闭 · Ctrl + Alt + T 开启"); return true; }
            try {
                activity=new InputActivity(); activity.CanReplaceWithTab=CanTabReplace; activity.ReplaceWithTab=QueueTabReplace; monitor=new InputMonitor(); enabled=true; recovery.Reset(); readerBlocked=false; timer.Start();
                revision=activity.Revision; serial=activity.Serial;
                status(EnglishCorrection ? "已开启 · 中文转英文，英文自动纠错" : "已开启 · 中文转英文，英文纠错已关闭"); return true;
            } catch(Exception error) {
                if(monitor!=null) { monitor.Dispose(); monitor=null; } if(activity!=null) { activity.Dispose(); activity=null; }
                status(error.Message); return false;
            }
        }
        public void Dismiss() { gate.Reset(activity==null ? 0 : activity.Serial); receipt=false; Cancel(); shown=null; panel.Hide(); feedback.Hide(); }
        public void Recover() {
            if(!enabled || disposed) return;
            Cancel(); gate.Observe(null,activity.Serial,clock.ElapsedMilliseconds,true,activity.LastFocusHandle,EnglishCorrection);
            panel.Hide(); feedback.Hide(); shown=null; receipt=false;
            if(monitor!=null) monitor.Dispose(); monitor=null;
            recovery.Reset(); readerBlocked=true; blockedFocus=InputReader.FocusWindow().ToInt64(); blockedRevision=activity.Revision;
            status("正在恢复输入检测…");
        }
        private void Cancel() { if(request!=null) { request.Cancel(); request=null; } }
        private bool CanTabReplace() { return enabled && !disposed && !replacing && shown!=null && monitor!=null && monitor.Fresh && activity.Revision==requestRevision && panel.CanReplace && panel.IsShown && InputReader.FocusWindow().ToInt64()==shown.FocusHandle && InputReader.SameSource(shown,monitor.Latest) && !InputReader.CompositionUiVisible(); }
        private void QueueTabReplace() { if(!CanTabReplace()) return; try { panel.BeginInvoke(new Action(async ()=> { if(CanTabReplace()) await panel.ApplyReplacementAsync(); })); } catch(InvalidOperationException) { } }
        private void Tick(object sender,EventArgs e)
        {
            if(!enabled || disposed) return;
            if(Suspended) { Dismiss(); return; }
            if(ExcludeFocus!=null && ExcludeFocus(InputReader.FocusWindow().ToInt64())) { Dismiss(); return; }
            if(AllowedWindow!=0 && InputReader.RootWindow(InputReader.FocusWindow())!=AllowedWindow) { Dismiss(); return; }
            if(ExcludedFocusHandle!=0 && InputReader.FocusWindow().ToInt64()==ExcludedFocusHandle) { Dismiss(); return; }
            if(Native.Down((int)Keys.Escape)) { Dismiss(); return; }
            if(readerBlocked) {
                if(!recovery.Due(clock.ElapsedMilliseconds,InputReader.FocusWindow().ToInt64()!=blockedFocus || activity.Revision!=blockedRevision)) return;
                blockedFocus=InputReader.FocusWindow().ToInt64(); blockedRevision=activity.Revision;
                try { monitor=new InputMonitor(); readerBlocked=false; status("正在恢复输入助手…"); }
                catch { recovery.Failed(clock.ElapsedMilliseconds); }
                return;
            }
            monitor.UpdateActivity(activity);
            if(revision!=activity.Revision) {
                if(serial==activity.Serial) gate.Reset(activity.Serial);
                revision=activity.Revision; serial=activity.Serial; receipt=false; Cancel(); panel.Hide(); feedback.Hide(); shown=null;
            }
            if(replacing) return;
            if(receipt && InputReader.FocusWindow().ToInt64()!=receiptFocus) { receipt=false; panel.Hide(); }
            if(!monitor.Healthy) {
                receipt=false; Cancel(); gate.Observe(null,activity.Serial,clock.ElapsedMilliseconds,enabled,activity.LastFocusHandle,EnglishCorrection); panel.Hide(); feedback.Hide(); shown=null; monitor.Dispose(); monitor=null;
                recovery.Failed(clock.ElapsedMilliseconds); readerBlocked=true; blockedFocus=InputReader.FocusWindow().ToInt64(); blockedRevision=activity.Revision;
                status(recovery.Failures<=3 ? "输入读取超时 · 即将自动恢复" : "输入读取暂不可用 · 30 秒后重试，点击或输入可立即重试");
                return;
            }
            if(monitor.HasResponse) recovery.Healthy(clock.ElapsedMilliseconds);
            if(monitor.HasResponse && !monitor.Fresh) {
                Cancel(); gate.Observe(null,activity.Serial,clock.ElapsedMilliseconds,true,activity.LastFocusHandle,EnglishCorrection);
                feedback.Hide(); if(!receipt) panel.Hide(); shown=null;
                return;
            }
            InputSnapshot snapshot=monitor.Latest; int previous=gate.Generation;
            bool due=gate.Observe(snapshot,activity.Serial,clock.ElapsedMilliseconds,enabled,activity.LastFocusHandle,EnglishCorrection);
            if(previous!=gate.Generation) { Cancel(); feedback.Hide(); if(!receipt) panel.Hide(); shown=null; }
            if(snapshot!=null && snapshot.Error.Length>0) status(snapshot.Error);
            if(snapshot!=null && snapshot.Error.Length>0 && !snapshot.Error.Contains("密码") && activity.Serial!=lastErrorSerial && activity.LastFocusHandle==InputReader.FocusWindow().ToInt64()) {
                lastErrorSerial=activity.Serial; Point point=Cursor.Position;
                feedback.Display(new InputSnapshot { X=point.X,Y=point.Y,Height=12 },"此输入框暂不支持 · 可在主窗口翻译",2500);
            }
            UpdateFeedback();
            if(due) Translate(gate.Source,gate.Generation);
        }
        private async void Translate(InputSnapshot source,int generation)
        {
            receipt=false; Cancel(); CancellationTokenSource owned=new CancellationTokenSource(); request=owned; shown=source; requestRevision=activity.Revision;
            requestAt=clock.ElapsedMilliseconds; feedback.Hide();
            long expectedRevision=requestRevision;
            if(autoReplace && source.Position.StartsWith("qt:")) InputReader.StampPointer(source);
            bool correctionOnly=!InputTranslationGate.HasChinese(source.Text);
            panel.Pending(source,false,correctionOnly,EnglishCorrection);
            if(!correctionOnly) panel.ShowNear(source);
            status(correctionOnly ? "正在检查英文…" : EnglishCorrection ? "正在纠错并翻译…" : "正在翻译…");
            try {
                string english=await translate(source.Text,owned.Token);
                // 先处理已排队的新输入，避免即时返回的纠错结果抢在键入内容之前。
                await Task.Yield();
                if(!Current(source,generation,owned,expectedRevision)) return;
                if(correctionOnly && (InputTranslationGate.HasChinese(english) || String.IsNullOrWhiteSpace(english))) throw new InvalidOperationException("英文纠错结果无效 · 保留原文，请重试");
                gate.Complete(generation);
                if(correctionOnly && english.Trim()==source.Text.Trim()) { panel.Hide(); shown=null; feedback.Display(source,"检查完成 · 未发现明显英文错误",1500); status("检查完成 · 未发现明显英文错误"); return; }
                feedback.Hide();
                panel.Result(source,english,false,correctionOnly); if(correctionOnly) panel.ShowNear(source);
                status(correctionOnly ? "英文已纠正 · Tab 替换，也可复制或朗读" : "译文已显示 · 点击复制或替换");
                if(autoReplace) {
                    replacing=true; status("正在核对原文并自动替换…");
                    try {
                        string error=await InputMonitor.ReplaceAsync(source,english,owned.Token);
                        if(disposed || !enabled || owned.IsCancellationRequested) return;
                        if(error.Length==0) { gate.Reset(activity.Serial); shown=null; receipt=true; receiptFocus=source.FocusHandle; panel.Replaced(true); status(source.Mode=="terminal" ? "已自动替换成英文 · 尚未执行或发送" : "已自动替换成英文 · 未发送，可继续输入"); }
                        else { panel.ReplacementFailed(error); status("自动替换未完成 · "+error); }
                    } finally { replacing=false; }
                }
            } catch(OperationCanceledException) { }
            catch(Exception error) {
                if(Current(source,generation,owned,expectedRevision)) {
                    feedback.Hide(); gate.Complete(generation); panel.Failure(error.Message); panel.ShowNear(source); status(error.Message);
                }
            }
            finally { if(request==owned) request=null; owned.Dispose(); }
        }
        private bool Current(InputSnapshot source,int generation,CancellationTokenSource owned,long expectedRevision) { return !disposed && enabled && !owned.IsCancellationRequested && generation==gate.Generation && activity.Revision==expectedRevision && monitor!=null && monitor.Fresh && InputReader.FocusWindow().ToInt64()==source.FocusHandle && InputReader.SameSource(source,monitor.Latest); }
        internal void UpdateFeedback() {
            if(request==null || shown==null || replacing || panel.IsShown || InputTranslationGate.HasChinese(shown.Text) || clock.ElapsedMilliseconds-requestAt<800) return;
            if(Current(shown,gate.Generation,request,requestRevision)) feedback.Display(shown,clock.ElapsedMilliseconds-requestAt>=5000 ? "仍在检查英文 · 可继续输入，旧结果会取消" : "正在检查英文…");
            else feedback.Hide();
        }
        public void Dispose() { if(disposed) return; SetEnabled(false); disposed=true; timer.Dispose(); panel.Dispose(); feedback.Dispose(); }
    }
}
