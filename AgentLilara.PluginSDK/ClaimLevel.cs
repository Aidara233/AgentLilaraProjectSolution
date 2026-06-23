// AgentLilara.PluginSDK/ClaimLevel.cs
namespace AgentLilara.PluginSDK;

/// <summary>
/// 频道占用级别：Listen=只读旁听，Full=接管 reply + 抑制 ChannelEngine
/// </summary>
public enum ClaimLevel
{
    Listen,
    Full
}
