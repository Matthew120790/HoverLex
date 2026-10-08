using System;
using System.Threading;
using System.Threading.Tasks;

namespace HoverLex
{
    internal sealed class TranslationServiceException : InvalidOperationException
    {
        internal readonly bool Retryable;
        internal readonly int DelayMilliseconds;
        internal TranslationServiceException(string message,bool retryable,int delayMilliseconds=1000) : base(message) { Retryable=retryable; DelayMilliseconds=Math.Max(100,Math.Min(3000,delayMilliseconds)); }
    }

    internal static class RequestReliability
    {
        internal static async Task<T> RetryAsync<T>(Func<CancellationToken,Task<T>> operation,CancellationToken cancellation,Func<int,CancellationToken,Task> delay=null)
        {
            for(int attempt=0;;attempt++) {
                cancellation.ThrowIfCancellationRequested();
                int retryDelay;
                try { return await operation(cancellation).ConfigureAwait(false); }
                catch(TranslationServiceException error) {
                    if(!error.Retryable || attempt>=1) throw;
                    retryDelay=error.DelayMilliseconds;
                }
                await (delay==null ? Task.Delay(retryDelay,cancellation) : delay(retryDelay,cancellation)).ConfigureAwait(false);
            }
        }
        internal static async Task<T> WithDeadlineAsync<T>(Func<CancellationToken,Task<T>> operation,int milliseconds,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using(var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using(var timer=CancellationTokenSource.CreateLinkedTokenSource(cancellation)) {
                Task<T> work=operation(linked.Token);
                Task timeout=Task.Delay(milliseconds,timer.Token);
                try {
                    if(await Task.WhenAny(work,timeout).ConfigureAwait(false)==work) { T result=await work.ConfigureAwait(false); cancellation.ThrowIfCancellationRequested(); return result; }
                    linked.Cancel();
                    Observe(work);
                    cancellation.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("处理超时 · 原输入已保留，请重试");
                } finally { timer.Cancel(); }
            }
        }
        private static void Observe(Task work) { work.ContinueWith(task=> { task.Exception.Handle(error=>true); },CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default); }
    }
}
