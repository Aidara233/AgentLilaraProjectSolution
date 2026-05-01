using System;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 说话模块。监听工具执行事件，说话工具成功时触发回调。
    /// </summary>
    internal class SpeakModule : EngineModule
    {
        public override string Name => "说话";

        /// <summary>由 ChannelEngine 注入：发送消息到频道。</summary>
        public Func<string, Task>? OnSpeak { get; set; }

        /// <summary>本轮是否有说话动作（供 LoopControlModule 判断静默）。</summary>
        public bool HadSpeakThisRound { get; private set; }

        public override void Attach(ILoopBus bus)
        {
            bus.Subscribe<ToolExecutedEvent>(e =>
            {
                if (e.Call.Tool == "说话" && e.Result.IsSuccess && OnSpeak != null)
                {
                    OnSpeak(e.Result.Data ?? "").GetAwaiter().GetResult();
                    HadSpeakThisRound = true;
                }
            });
        }

        /// <summary>每轮开始前重置。</summary>
        public void ResetRound()
        {
            HadSpeakThisRound = false;
        }

        public override void Reset()
        {
            HadSpeakThisRound = false;
            OnSpeak = null;
        }
    }
}
