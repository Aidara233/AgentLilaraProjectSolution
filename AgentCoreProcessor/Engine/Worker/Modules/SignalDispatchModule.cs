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
                    case "记忆":
                        OnMemory?.Invoke(data).GetAwaiter().GetResult();
                        break;
                    case "睡眠许可":
                        OnSignal?.Invoke("dream-permission", null).GetAwaiter().GetResult();
                        break;
                    case "强制睡觉":
                        OnSignal?.Invoke("force-sleep", null).GetAwaiter().GetResult();
                        break;
                    case "修改睡眠配置":
                        OnSignal?.Invoke("dream-config", data).GetAwaiter().GetResult();
                        break;
                    case "调整睡意":
                        OnSignal?.Invoke("sleep-score-offset", data).GetAwaiter().GetResult();
                        break;
                    case "触发红色警报":
                        OnSignal?.Invoke("red-alert", null).GetAwaiter().GetResult();
                        break;
                    case "标记复盘":
                        OnReviewHint?.Invoke(data).GetAwaiter().GetResult();
                        break;
                    case "报警":
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
