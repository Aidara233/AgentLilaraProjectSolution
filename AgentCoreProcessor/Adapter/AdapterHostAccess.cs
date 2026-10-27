using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Adapter;

internal sealed class AdapterHostAccess(MasterEngine engine) : IAdapterHostAccess
{
    public bool IsReady => engine.Session is not null;
    public bool IsIdle => engine.IsIdle;
    public bool HasBusyWorker() => engine.HasBusyWorker();
    private static AdapterMessageView Map(UserMessage m) => new(m.Id, m.UserId, m.SenderName, m.Content, m.IsFromBot, m.Time, m.PlatformMessageId);
    public async Task<AdapterChannelView?> FindChannelByNameAsync(string name)
    {
        var channel = await engine.Session.FindChannelByNameAsync(name);
        return channel is null ? null : new(channel.Id, channel.Name);
    }
    public async Task<List<AdapterChannelView>> GetAllChannelsAsync() =>
        (await engine.Session.GetAllChannelsAsync()).Select(c => new AdapterChannelView(c.Id, c.Name)).ToList();
    public async Task<List<AdapterMessageView>> GetContextByChannelAsync(int id, int limit) =>
        (await engine.Session.GetContextByChannelAsync(id, limit)).Select(Map).ToList();
    public async Task<List<AdapterMessageView>> GetMessagesAfterIdAsync(int id, int afterId, int limit) =>
        (await engine.Session.GetMessagesAfterIdAsync(id, afterId, limit)).Select(Map).ToList();
    public async Task<List<AdapterMessageView>> GetMessagesBeforeIdAsync(int id, int beforeId, int limit) =>
        (await engine.Session.GetMessagesBeforeIdAsync(id, beforeId, limit)).Select(Map).ToList();
    public async Task<List<AdapterMessageView>> SearchMessagesByChannelAsync(int id, string keyword, int offset, int limit) =>
        (await engine.Session.SearchMessagesByChannelAsync(id, keyword, offset, limit)).Select(Map).ToList();
}
