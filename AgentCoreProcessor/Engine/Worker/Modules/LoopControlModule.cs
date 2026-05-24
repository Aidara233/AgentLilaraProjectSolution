namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 循环控制模块。跟踪轮次和静默轮次，注入循环状态到 prompt。
    /// </summary>
    internal class LoopControlModule : EngineModule
    {
        public override string Name => "循环控制";

        public int MaxRounds { get; set; } = 30;
        public int MaxSilentRounds { get; set; } = 5;

        public int TotalRounds { get; private set; }
        public int SilentRounds { get; private set; }

        /// <summary>连续输出轮次（仅 speak/send_media/wait/deescalate，无实际工作）。</summary>
        public int ConsecutiveOutputOnly { get; set; }

        /// <summary>频道标识，注入 prompt 供模型使用（如 thinking_notes 的 notebook 参数）。</summary>
        public string? ChannelId { get; set; }

        /// <summary>上一轮是否只调了输出工具（用于注入确认提示）。</summary>
        public bool WasOutputOnly { get; set; }

        public override void Attach(ILoopBus bus) { }

        /// <summary>轮次推进。hadSpeak=true 时重置静默计数。</summary>
        public void AdvanceRound(bool hadSpeak)
        {
            TotalRounds++;
            if (hadSpeak)
                SilentRounds = 0;
            else
                SilentRounds++;
        }

        /// <summary>用户发新消息时重置所有计数。</summary>
        public void OnNewMessage()
        {
            TotalRounds = 0;
            SilentRounds = 0;
            ConsecutiveOutputOnly = 0;
        }

        public bool IsMaxSilentReached => SilentRounds >= MaxSilentRounds;
        public bool IsMaxRoundsReached => TotalRounds >= MaxRounds;

        public override void Reset()
        {
            TotalRounds = 0;
            SilentRounds = 0;
            ConsecutiveOutputOnly = 0;
        }
    }
}
