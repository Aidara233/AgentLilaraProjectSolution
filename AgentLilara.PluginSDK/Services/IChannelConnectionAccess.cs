namespace AgentLilara.PluginSDK.Services;

/// <summary>会话连接频道所需的窄端口；占用与消息订阅由宿主协调。</summary>
public interface IChannelConnectionAccess
{
    bool ClaimChannel(string sessionId, int channelId, ClaimLevel level);
    void ReleaseChannel(string sessionId, int channelId);
    IInputPort SubscribeChannel(int channelId, string subStream, int weight);
    void UnsubscribeChannel(IInputPort port);
    Task<string?> GetChannelNameAsync(int channelId);
}
