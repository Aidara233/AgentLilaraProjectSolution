namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// Speak 工具防话唠守卫。追踪连续纯发言轮次，达到阈值后阻断 speak 调用。
    /// </summary>
    public interface ISpeakGuard
    {
        int ConsecutiveSpeakRounds { get; }
        bool IsBlocked { get; }
    }
}
