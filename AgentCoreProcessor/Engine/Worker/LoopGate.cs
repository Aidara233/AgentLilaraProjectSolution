using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 循环闸门。auto-reset 语义：放行后立即重置，新 Signal 打在新闸门上。
    /// </summary>
    internal class LoopGate
    {
        private volatile TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>放行闸门（幂等，多次调用不累积）。</summary>
        public void Signal()
        {
            _tcs.TrySetResult();
        }

        /// <summary>等待放行，通过后立即重置。返回 false 表示超时。</summary>
        public async Task<bool> WaitAsync(System.TimeSpan timeout, CancellationToken ct = default)
        {
            var current = _tcs;
            var triggered = current.Task == await Task.WhenAny(current.Task, Task.Delay(timeout, ct));
            Interlocked.CompareExchange(
                ref _tcs,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                current);
            return triggered;
        }
    }
}
