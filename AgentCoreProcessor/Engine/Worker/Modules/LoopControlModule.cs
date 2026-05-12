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

        /// <summary>频道标识，注入 prompt 供模型使用（如 thinking_notes 的 notebook 参数）。</summary>
        public string? ChannelId { get; set; }

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
        }

        public bool IsMaxSilentReached => SilentRounds >= MaxSilentRounds;
        public bool IsMaxRoundsReached => TotalRounds >= MaxRounds;

        public override string? BuildPromptSection(EngineMode mode)
        {
            if (mode == EngineMode.Express) return null;

            var sb = new System.Text.StringBuilder();
            sb.Append($"[循环状态] 第{TotalRounds + 1}轮/共{MaxRounds}轮，静默{SilentRounds}轮/上限{MaxSilentRounds}轮");

            if (!string.IsNullOrEmpty(ChannelId))
                sb.Append($"\n当前频道ID: {ChannelId}（thinking_notes 的 notebook 参数用这个）");

            // 最后一轮警告
            if (TotalRounds + 1 >= MaxRounds)
                sb.Append("\n⚠️ 这是最后一轮！你必须在本轮用 speak 向用户汇报进度并请求回应，否则循环将强制结束。");
            else if (SilentRounds + 1 >= MaxSilentRounds)
                sb.Append("\n⚠️ 静默即将达到上限！你必须在本轮用 speak 向用户汇报当前进度。");

            return sb.ToString();
        }

        public override void Reset()
        {
            TotalRounds = 0;
            SilentRounds = 0;
        }
    }
}
