using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 循环闸门。auto-reset 语义：放行后立即重置，新 Signal 打在新闸门上。
    /// _signalPending 解决在 WaitAsync 调用之间 Signal 被残留 waiter 吃掉的问题。
    /// </summary>
    internal class LoopGate
    {
        private volatile TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile int _signalPending;

        /// <summary>放行闸门。幂等：多次调用等价于一次，直到被 WaitAsync 消费。</summary>
        public void Signal()
        {
            if (Interlocked.Exchange(ref _signalPending, 1) == 0)
                _tcs.TrySetResult();
        }

        /// <summary>等待放行，通过后立即重置。返回 false 表示超时。</summary>
        public async Task<bool> WaitAsync(System.TimeSpan timeout, CancellationToken ct = default)
        {
            // 先消费 pending 信号（来自上次 WaitAsync 返回后到本次 WaitAsync 之间的 Signal）
            if (Interlocked.CompareExchange(ref _signalPending, 0, 1) == 1)
            {
                // 提前返回也必须替换 TCS，防止旧的已完成 TCS 在下一次 WaitAsync 中被误消费
                var old = Interlocked.Exchange(ref _tcs,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                return true;
            }

            var current = _tcs;
            var triggered = current.Task == await Task.WhenAny(current.Task, Task.Delay(timeout, ct));

            // 如果在等待期间 Signal() 被调用，可能刚设置了 _tcs 但还没来得及被看到，
            // 此时 _signalPending 一定为 1
            if (Interlocked.CompareExchange(ref _signalPending, 0, 1) == 1)
                triggered = true;

            Interlocked.CompareExchange(
                ref _tcs,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                current);
            return triggered;
        }
    }
}
