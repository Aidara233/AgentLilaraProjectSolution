using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Engine.Context;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ChannelConnectionPortTests
{
    private sealed class Access : IChannelConnectionAccess
    {
        public bool Granted = true;
        public List<string> Calls { get; } = new();
        public bool ClaimChannel(string sessionId, int channelId, ClaimLevel level)
        { Calls.Add($"claim:{channelId}:{level}"); return Granted; }
        public void ReleaseChannel(string sessionId, int channelId) => Calls.Add($"release:{channelId}");
        public IInputPort SubscribeChannel(int channelId, string stream, int weight)
        { Calls.Add($"subscribe:{channelId}:{stream}:{weight}"); return new Port($"{channelId}:{stream}", weight); }
        public void UnsubscribeChannel(IInputPort port) => Calls.Add("unsubscribe:" + port.Name);
        public Task<string?> GetChannelNameAsync(int channelId)
        { Calls.Add($"name:{channelId}"); return Task.FromResult<string?>("group_42"); }
    }
    private sealed class Port(string name, int weight) : IInputPort
    {
        public string Name => name;
        public int Weight => weight;
        public Type DataType => typeof(string);
        public Task<object?> ReceiveAsync(CancellationToken ct) => Task.FromResult<object?>(null);
        public bool TryPeek(out object? data) { data = null; return false; }
    }
    private static RuntimeSession Session(Access access)
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        session.SetChannelConnectionAccess(access);
        return session;
    }

    [Fact]
    public async Task FullConnectionThenListenReleasesOldPortsBeforeClaimingNewChannel()
    {
        var access = new Access();
        var session = Session(access);
        var unrelated = new Port("mailbox", 100);
        session.AddInputPort("mailbox", unrelated);
        await session.ConnectChannelAsync(42, ConnectionMode.Full);
        Assert.True(session.ConnectedChannel.CanSpeak);
        Assert.Equal(3, session.Inputs.Count);
        await session.ConnectChannelAsync(43, ConnectionMode.Listen);
        Assert.Equal(new[] { "claim:42:Full", "subscribe:42:normal:5", "subscribe:42:mention:100",
            "unsubscribe:42:normal", "unsubscribe:42:mention", "release:42",
            "claim:43:Listen", "subscribe:43:normal:5" }, access.Calls);
        Assert.False(session.ConnectedChannel.CanSpeak);
        Assert.Equal(43, session.ConnectedChannel.CurrentChannelId);
        Assert.Equal(2, session.Inputs.Count);
        session.DisconnectChannel();
        Assert.Same(unrelated, Assert.Single(session.Inputs).Value);
        Assert.Equal(ConnectionMode.None, session.ConnectedChannel.Mode);
        Assert.Equal(new[] { "unsubscribe:43:normal", "release:43" }, access.Calls.TakeLast(2));
        var count = access.Calls.Count;
        session.DisconnectChannel();
        Assert.Equal(count, access.Calls.Count);
    }

    [Fact]
    public async Task DeniedFullClaimDoesNotSubscribeOrSetConnection()
    {
        var access = new Access { Granted = false };
        var session = Session(access);
        await session.ConnectChannelAsync(42, ConnectionMode.Full);
        Assert.Equal(new[] { "claim:42:Full" }, access.Calls);
        Assert.Empty(session.Inputs);
        Assert.Equal(0, session.ConnectedChannel.CurrentChannelId);
        Assert.Null(session.BuildStateSnapshot().ConnectedChannelId);
    }

    [Fact]
    public async Task NameQueryAndHostAttachmentUsePortWithoutConcreteEngines()
    {
        var access = new Access();
        var session = Session(access);
        var host = new SessionContextHost(Array.Empty<(int, string?, bool)>(), null,
            new ChannelContextService(new FakeDataSource()));
        session.SetContextHost(host);
        await session.ConnectChannelAsync(42, ConnectionMode.Listen);
        Assert.Equal(new[] { "claim:42:Listen", "subscribe:42:normal:5", "name:42" }, access.Calls);
        Assert.Equal("group_42", host.StateFor(42).ChannelName);
        Assert.Equal("42", session.BuildStateSnapshot().ConnectedChannelId);
        session.DisconnectChannel();
        Assert.Empty(host.ChannelIds);
    }
}
