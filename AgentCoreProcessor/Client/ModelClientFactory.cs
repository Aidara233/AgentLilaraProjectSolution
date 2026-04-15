namespace AgentCoreProcessor.Client
{
    public static class ModelClientFactory
    {
        public static IModelClient Create(ApiClientCfg cfg)
        {
            return cfg.Provider?.ToLowerInvariant() switch
            {
                "claude" or "anthropic" => new ClaudeModelClient(cfg),
                _ => new OpenAIModelClient(cfg),
            };
        }
    }
}
