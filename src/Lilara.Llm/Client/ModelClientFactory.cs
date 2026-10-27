namespace AgentCoreProcessor.Client
{
    public static class ModelClientFactory
    {
        public static IModelClient Create(ApiClientCfg cfg, string? claudeDumpDirectory = null)
        {
            return cfg.Provider?.ToLowerInvariant() switch
            {
                "claude" or "anthropic" => new ClaudeModelClient(cfg, claudeDumpDirectory),
                _ => new OpenAIModelClient(cfg),
            };
        }
    }
}
