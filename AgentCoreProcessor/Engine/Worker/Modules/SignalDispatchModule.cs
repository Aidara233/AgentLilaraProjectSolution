using System;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 信号分发模块。监听工具执行事件，将信号类工具的结果转发到全局 EventBus。
    /// 覆盖：记忆存储、睡眠许可、强制睡觉、修改睡眠配置、调整睡意、红色警报、标记复盘、报警。
    /// </summary>
    internal class SignalDispatchModule : EngineModule
    {
        public override string Name => "信号分发";

        /// <summary>记忆存储回调。</summary>
        public Func<string, Task>? OnMemory { get; set; }

        /// <summary>信号发射回调（信号名, 载荷）。</summary>
        public Func<string, string?, Task>? OnSignal { get; set; }

        /// <summary>标记复盘回调。</summary>
        public Func<string, Task>? OnReviewHint { get; set; }

        /// <summary>报警回调。</summary>
        public Func<string, Task>? OnAlert { get; set; }

        public override void Attach(ILoopBus bus)
        {
            bus.Subscribe<ToolExecutedEvent>(e =>
            {
                if (!e.Result.IsSuccess) return;
                var data = e.Result.Data ?? "";

                switch (e.Call.Tool)
                {
                    case "memory":
                        OnMemory?.Invoke(data).GetAwaiter().GetResult();
                        break;
                    case "dream_permission":
                        OnSignal?.Invoke("dream-permission", null).GetAwaiter().GetResult();
                        break;
                    case "force_sleep":
                        OnSignal?.Invoke("force-sleep", null).GetAwaiter().GetResult();
                        break;
                    case "dream_config":
                        OnSignal?.Invoke("dream-config", data).GetAwaiter().GetResult();
                        break;
                    case "adjust_sleep_score":
                        OnSignal?.Invoke("sleep-score-offset", data).GetAwaiter().GetResult();
                        break;
                    case "trigger_red_alert":
                        OnSignal?.Invoke("red-alert", null).GetAwaiter().GetResult();
                        break;
                    case "mark_review_hint":
                        OnReviewHint?.Invoke(data).GetAwaiter().GetResult();
                        break;
                    case "alert":
                        OnAlert?.Invoke(data).GetAwaiter().GetResult();
                        break;
                }
            });
        }

        public override void Reset()
        {
            OnMemory = null;
            OnSignal = null;
            OnReviewHint = null;
            OnAlert = null;
        }
    }
}
