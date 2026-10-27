namespace AgentLilara.PluginSDK.Services;

/// <summary>频道当前占用会话的只读快照，不包含运行时实例引用。</summary>
public sealed record ChannelSessionView(int ChannelId, string SessionId, string Type,
    bool IsAlive, DateTime LastActivity, int RoundCount);

public interface IChannelSessionQuery
{
    ChannelSessionView? GetChannelSessionSnapshot(int channelId);
    List<ChannelSessionView> GetChannelSessionSnapshots();
}
