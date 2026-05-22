using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 循环闸门。封装"等触发→判条件→跑执行"的循环骨架。
    /// 使用 delegate 而非抽象方法，引擎组合 Gate 而不继承。
    /// </summary>
    internal class Gate
    {
        private readonly LoopGate _inner = new();
        private readonly EventBus _eventBus;
        private volatile bool _forceWake;

        /// <summary>最近一次触发事件的信号追踪 ID（供引擎建立因果链）。</summary>
        public string? LastTriggerSignalId { get; private set; }
        /// <summary>最近一次触发事件的父 span ID。</summary>
        public string? LastTriggerSpanId { get; private set; }

        public Gate(EventBus eventBus)
        {
            _eventBus = eventBus;
        }

        /// <summary>内部唤醒（引擎/定时器回调调用）。</summary>
        public void Signal() => _inner.Signal();

        /// <summary>强制唤醒，跳过 ShouldActivate 直接开闸。</summary>
        public void ForceWake()
        {
            _forceWake = true;
            _inner.Signal();
        }

        /// <summary>引擎注入：评估是否开闸。返回 true 开闸。</summary>
        public Func<Task<bool>>? ShouldActivate { get; set; }

        /// <summary>引擎注入：事件过滤器。返回 true 表示此事件应唤醒闸门。为 null 时所有事件都唤醒。</summary>
        public Func<EngineEvent, bool>? EventFilter { get; set; }

        /// <summary>引擎注入：执行本轮工作。</summary>
        public Func<CancellationToken, Task>? ExecuteAsync { get; set; }

        /// <summary>
        /// 等待任一触发源：EventBus 事件 / Signal() / 超时 / CTS 取消。
        /// </summary>
        public async Task<bool> WaitForTriggerAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            var busTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void handler(EngineEvent e)
            {
                if (EventFilter != null && !EventFilter(e))
                    return;
                LastTriggerSignalId = e.TraceSignalId;
                LastTriggerSpanId = e.TraceParentSpanId;
                busTcs.TrySetResult(true);
            }

            _eventBus.OnEvent += handler;
            try
            {
                var innerTask = _inner.WaitAsync(timeout, ct);
                var completed = await Task.WhenAny(innerTask, busTcs.Task);

                if (completed == busTcs.Task)
                    return true;

                return await innerTask;
            }
            finally
            {
                _eventBus.OnEvent -= handler;
            }
        }

        /// <summary>循环骨架。引擎调用此方法启动循环，不再写 while。</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await WaitForTriggerAsync(Timeout.InfiniteTimeSpan, ct);

                bool isForceWake = _forceWake;
                _forceWake = false;

                if (!isForceWake && ShouldActivate != null && !await ShouldActivate())
                    continue;

                if (ExecuteAsync != null)
                    await ExecuteAsync(ct);
            }
        }
    }
}
