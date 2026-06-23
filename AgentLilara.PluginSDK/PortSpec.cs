// AgentLilara.PluginSDK/PortSpec.cs
namespace AgentLilara.PluginSDK;

/// <summary>
/// 端口配置，描述一个订阅频道+子流+权重。
/// </summary>
public class PortSpec
{
    public int ChannelId { get; set; }
    public string SubStream { get; set; } = "normal";  // "normal" / "mention" / "command"
    public int Weight { get; set; } = 5;

    /// <summary>是否在 session 启动时预加载历史消息，默认 true</summary>
    public bool PreloadHistory { get; set; } = true;
}
