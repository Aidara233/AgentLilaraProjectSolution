namespace AgentCoreProcessor.Adapter;

public sealed record AdapterChannelView(int Id, string Name);
public sealed record AdapterMessageView(int Id, int UserId, string SenderName, string Content,
    bool IsFromBot, DateTime Time, string? PlatformMessageId);

/// <summary>HTTP 桥所需的宿主查询能力，返回独立快照。</summary>
public interface IAdapterHostAccess
{
    bool IsReady { get; }
    bool IsIdle { get; }
    bool HasBusyWorker();
    Task<AdapterChannelView?> FindChannelByNameAsync(string name);
    Task<List<AdapterChannelView>> GetAllChannelsAsync();
    Task<List<AdapterMessageView>> GetContextByChannelAsync(int channelId, int limit);
    Task<List<AdapterMessageView>> GetMessagesAfterIdAsync(int channelId, int afterId, int limit);
    Task<List<AdapterMessageView>> GetMessagesBeforeIdAsync(int channelId, int beforeId, int limit);
    Task<List<AdapterMessageView>> SearchMessagesByChannelAsync(int channelId, string keyword, int offset, int limit);
}

internal interface IHostAwareAdapter
{
    void AttachHost(IAdapterHostAccess host);
}
