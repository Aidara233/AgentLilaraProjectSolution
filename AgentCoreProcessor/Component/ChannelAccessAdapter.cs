using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Component;

internal class ChannelAccessAdapter : IChannelAccess
{
    private readonly Engine.MasterEngine _master;

    public ChannelAccessAdapter(Engine.MasterEngine master)
    {
        _master = master;
    }

    public void NotifyChannel(int channelId, string content)
    {
        _master.NotifyChannel(channelId, content);
    }

    public Task<List<ChannelSummary>> GetAllChannelsAsync()
    {
        // TODO: implement when needed
        return Task.FromResult(new List<ChannelSummary>());
    }

    public Task<ChannelDetail?> GetChannelDetailAsync(int channelId)
    {
        // TODO: implement when needed
        return Task.FromResult<ChannelDetail?>(null);
    }

    public Task<List<MessageSummary>> GetMessagesAsync(int channelId, int count = 20)
    {
        // TODO: implement when needed
        return Task.FromResult(new List<MessageSummary>());
    }

    public Task UpdateAffinityAsync(int channelId, float delta)
    {
        // TODO: implement when needed (component side uses ChannelAccessImpl)
        return Task.CompletedTask;
    }
}
