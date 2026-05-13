namespace AgentLilara.PluginSDK.Services
{
    public interface ILoopControl
    {
        EngineMode CurrentMode { get; }
        void SetMode(EngineMode mode, string? reason = null);
        void Signal();
    }
}
