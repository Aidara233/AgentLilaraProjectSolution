// AgentCoreProcessor/Engine/Session/ConnectedChannelState.cs
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// IConnectedChannel 的可变实现。每个 RuntimeSession 持有一份，注入到组件的 perLoopServices，
/// 供发送工具读取当前目标频道。状态变更由 RuntimeSession 的 ConnectChannel/DisconnectChannel 驱动。
/// </summary>
internal class ConnectedChannelState : IConnectedChannel
{
    public int CurrentChannelId { get; private set; }
    public ConnectionMode Mode { get; private set; } = ConnectionMode.None;
    public bool CanSpeak => Mode == ConnectionMode.Full;

    /// <summary>由 RuntimeSession 在 claim/订阅完成后更新。</summary>
    public void Set(int channelId, ConnectionMode mode)
    {
        CurrentChannelId = channelId;
        Mode = mode;
    }

    /// <summary>断开连接后归零。</summary>
    public void Clear()
    {
        CurrentChannelId = 0;
        Mode = ConnectionMode.None;
    }
}
