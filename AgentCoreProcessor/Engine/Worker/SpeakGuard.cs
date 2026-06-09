using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine
{
    internal class SpeakGuard : ISpeakGuard
    {
        public int ConsecutiveSpeakRounds { get; set; }
        public bool IsBlocked => ConsecutiveSpeakRounds >= 2;
    }
}
