namespace AgentCoreProcessor.Engine
{
    internal class AgentConfig
    {
        public int MaxRounds { get; set; } = 20;
        public int ExpressMaxRounds { get; set; } = 8;
        public int[] BackoffSeconds { get; set; } = { 10, 30, 60, 120, 300 };
        public int ModelCallMaxAttempts { get; set; } = 3;
        public int[] ModelCallRetryDelaySeconds { get; set; } = { 5, 15 };
        // 三层压缩阈值基于 200k 上下文模型设定：L1=80k / L2=130k / L3=150k
        // L1 ≈ 40% 上下文（温和提醒） / L2 ≈ 65%（明确催促） / L3 ≈ 75%（强制锁定工具表只剩 compact）
        // CompressMinTokens 用于过滤极短对话，避免误触发评估
        // CompressRetained* 字段在新 compact 工具方案下不再被使用，保留供未来兼容
        public int CompressL1Tokens { get; set; } = 80000;
        public int CompressL2Tokens { get; set; } = 130000;
        public int CompressL3Tokens { get; set; } = 150000;
        public int CompressMinTokens { get; set; } = 5000;
        public int CompressRetainedMessageCount { get; set; } = 6;
        public int CompressRetainedMaxTokens { get; set; } = 2000;
    }
}
