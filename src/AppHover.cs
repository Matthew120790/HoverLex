using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace HoverLex
{
    public sealed partial class MainForm
    {
        private async Task LookupTappedPoint(Point point,long focus)
        {
            if(IsDisposed || !settings.Enabled || dictionary==null || busy || (popup.Visible && popup.Bounds.Contains(point))) return;
            long revision=selectionTranslation.GestureRevision;
            busy=true; CancelHoverLookup(); popup.Hide(); status.Text="正在取词…";
            try {
                var result=await Task.Run(()=>Probe(point.X,point.Y,"uia",2800));
                if(IsDisposed || !settings.Enabled || revision!=selectionTranslation.GestureRevision || InputReader.SelectionFocusWindow().ToInt64()!=focus ||
                    Math.Abs(System.Windows.Forms.Cursor.Position.X-point.X)>3 || Math.Abs(System.Windows.Forms.Cursor.Position.Y-point.Y)>3 || Native.Down(0x11)) return;
                if(!String.IsNullOrWhiteSpace(result.Word)) { status.Text=result.Method+" · "+result.Word; ShowLookup(result,point,true); }
                else if(result.Method!="密码保护") { status.Text=result.Error; popup.SetFailure(result.Error); popup.ShowAt(point); hideAt=clock.ElapsedMilliseconds+6000; }
            } catch(Exception error) { if(!IsDisposed) status.Text="取词失败："+error.Message; }
            finally { busy=false; }
        }
        private readonly HoverTranslation hoverTranslation=new HoverTranslation();
        private CancellationTokenSource hoverRequest;
        private int hoverRevision;
        private Point? hoverAnchor;
        private void CancelHoverLookup()
        {
            hoverRevision++;
            CancellationTokenSource previous=hoverRequest; hoverRequest=null; hoverAnchor=null;
            if(previous!=null) previous.Cancel();
        }
        private void DismissHoverLookup()
        {
            CancelHoverLookup(); gate.Reset(); popup.Hide();
        }
        private async void ShowLookup(CaptureResult capture,Point location,bool mouseLookup)
        {
            CancelHoverLookup();
            Entry offline=dictionary.Lookup(capture.Word);
            TranslationOptions options=TranslationPreferences.Current;
            popup.SetResult(capture,offline); popup.ShowAt(location);
            hideAt=clock.ElapsedMilliseconds+(mouseLookup ? 8000 : 20000);
            if(previewMode || options.Provider!="deepseek") return;
            CancellationTokenSource owned=new CancellationTokenSource(); hoverRequest=owned;
            int revision=hoverRevision; hoverAnchor=mouseLookup ? (Point?)location : null;
            popup.SetProviderState(offline==null ? "DeepSeek · 正在解释词义…" : "DeepSeek 查询中 · 离线参考",null);
            // 联网期间保持卡片；松开 Ctrl 后仍可等待与阅读。
            hideAt=clock.ElapsedMilliseconds+35000;
            try {
                Entry translated=await hoverTranslation.LookupAsync(capture,offline,options,owned.Token);
                if(IsDisposed || owned.IsCancellationRequested || revision!=hoverRevision || !popup.Visible || options.Revision!=TranslationPreferences.Current.Revision) return;
                popup.SetResult(capture,translated);
                popup.SetProviderState(String.IsNullOrWhiteSpace(capture.Context) ? "DeepSeek · 单词释义" : "DeepSeek · 语境释义",null);
                hideAt=clock.ElapsedMilliseconds+(mouseLookup ? 8000 : 20000);
                status.Text="DeepSeek · "+capture.Word;
            } catch(OperationCanceledException) { }
            catch(Exception error) {
                if(!IsDisposed && !owned.IsCancellationRequested && revision==hoverRevision && popup.Visible) {
                    popup.SetProviderState(offline==null ? "DeepSeek · 查询未完成" : "DeepSeek 未完成 · 离线参考",error.Message);
                    status.Text=error.Message; hideAt=clock.ElapsedMilliseconds+15000;
                }
            } finally {
                if(hoverRequest==owned) { hoverRequest=null; hoverAnchor=null; }
                owned.Dispose();
            }
        }
    }
}
