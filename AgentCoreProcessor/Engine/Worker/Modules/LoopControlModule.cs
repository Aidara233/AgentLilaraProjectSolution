namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 循环控制模块。跟踪轮次和静默轮次，注入循环状态到 prompt。
    /// </summary>
    internal class LoopControlModule : EngineModule
    {
        public override string Name => "循环控制";
        public override int PromptPriority => 80;

        public int MaxRounds { get; set; } = 30;
        public int MaxSilentRounds { get; set; } = 5;

        public int TotalRounds { get; private set; }
        public int SilentRounds { get; private set; }

        public override void Attach(ILoopBus bus) { }

        /// <summary>轮次推进。返回 true 表示应继续循环。</summary>
        public bool AdvanceRound(bool hadSpeak)
        {
            TotalRounds++;
            if (hadSpeak)
                SilentRounds = 0;
            else
                SilentRounds++;

            return TotalRounds < MaxRounds;
        }

        /// <summary>新消息到达时重置静默计数。</summary>
        public void OnNewMessage()
        {
            SilentRounds = 0;
        }

        public bool IsMaxSilentReached => SilentRounds >= MaxSilentRounds;
        public bool IsMaxRoundsReached => TotalRounds >= MaxRounds;

        public override string? BuildPromptSection(EngineMode mode)
        {
            if (mode == EngineMode.Express) return null;
            return $"[循环状态] 第{TotalRounds + 1}轮/共{MaxRounds}轮，距上次交互{SilentRounds}轮/上限{MaxSilentRounds}轮";
        }

        public override void Reset()
        {
            TotalRounds = 0;
            SilentRounds = 0;
        }
    }
}
