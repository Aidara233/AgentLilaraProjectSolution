namespace AgentCoreProcessor.Engine
{
    internal class AgentConfig
    {
        public int MaxRounds { get; set; } = 20;
        public int[] BackoffSeconds { get; set; } = { 10, 30, 60, 120, 300 };
        public int ModelCallMaxAttempts { get; set; } = 3;
        public int[] ModelCallRetryDelaySeconds { get; set; } = { 5, 15 };
        public int CompressL1Tokens { get; set; } = 30000;
        public int CompressL2Tokens { get; set; } = 50000;
        public int CompressL3Tokens { get; set; } = 70000;
        public int CompressMinTokens { get; set; } = 5000;
        public int CompressRetainedMessageCount { get; set; } = 6;
        public int CompressRetainedMaxTokens { get; set; } = 2000;
    }
}
