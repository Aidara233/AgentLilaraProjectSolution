namespace AgentCoreProcessor.Engine;

/// <summary>频道占用对象的生命周期所有者。所有访问由宿主池协调锁串行化。</summary>
internal sealed class ChannelOccupancyRegistry
{
    private readonly Dictionary<int, ChannelOccupancyState> _states = new();

    public ChannelOccupancyState GetOrCreate(int channelId)
    {
        if (!_states.TryGetValue(channelId, out var state))
        {
            state = new ChannelOccupancyState();
            _states.Add(channelId, state);
        }
        return state;
    }

    public bool TryGet(int channelId, out ChannelOccupancyState? state) => _states.TryGetValue(channelId, out state);

    public bool Remove(int channelId, ChannelOccupancyState expected)
    {
        if (!_states.TryGetValue(channelId, out var current) || !ReferenceEquals(current, expected)) return false;
        return _states.Remove(channelId);
    }
}
