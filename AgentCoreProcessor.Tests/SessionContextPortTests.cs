using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Models;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SessionContextPortTests
{
    [Fact]
    public async Task ConnectionUsesContextPortAndSnapshotsItsCursorWithoutConcreteHost()
    {
        var calls = new List<string>();
        var context = new Context(calls);
        var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        var root = Path.Combine(Path.GetTempPath(), "lilara-context-" + Guid.NewGuid().ToString("N"));
        try
        {
            session.SetPersister(new SessionFileStore(root, session.SessionId));
            session.SetContextHost(context);
            session.SetChannelConnectionAccess(new Access(calls));
            await session.ConnectChannelAsync(42, ConnectionMode.Listen);
            Assert.Equal(new[] { "claim", "subscribe", "name", "attach:42:channel", "build:42" }, calls);
            Assert.Equal(77, session.BuildStateSnapshot().ChannelCursors[42]);
            session.DisconnectChannel();
            Assert.Empty(context.ChannelIds);
            Assert.Empty(session.BuildStateSnapshot().ChannelCursors);
            Assert.Equal(new[] { "unsubscribe", "release", "detach:42" }, calls.TakeLast(3));
            Assert.Equal("Lilara.Session", typeof(ISessionContext).Assembly.GetName().Name);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Context(List<string> calls) : ISessionContext
    {
        private readonly Dictionary<int, ChannelContextState> _states = new();
        public IEnumerable<int> ChannelIds => _states.Keys;
        public ChannelContextState StateFor(int channelId) => _states[channelId];
        public ChannelContextState AttachChannel(int channelId, string? channelName)
        {
            calls.Add($"attach:{channelId}:{channelName}");
            return _states[channelId] = new ChannelContextState { ChannelId = channelId, ChannelName = channelName, LastConsumedMessageId = 77 };
        }
        public void DetachChannel(int channelId) { calls.Add($"detach:{channelId}"); _states.Remove(channelId); }
        public Task<List<Message>> BuildChannelContextAsync(int channelId)
        { calls.Add($"build:{channelId}"); return Task.FromResult(new List<Message> { new() { Role = "user", Content = "context" } }); }
        public Task<List<Message>> BuildStartAsync() => throw new NotSupportedException();
        public Task<List<Message>> BuildRoundAsync(IEnumerable<(int channelId, UserMessage msg, bool isMention)> drained) => throw new NotSupportedException();
    }

    private sealed class Access(List<string> calls) : IChannelConnectionAccess
    {
        public bool ClaimChannel(string sessionId, int channelId, ClaimLevel level) { calls.Add("claim"); return true; }
        public void ReleaseChannel(string sessionId, int channelId) => calls.Add("release");
        public IInputPort SubscribeChannel(int channelId, string subStream, int weight)
        { calls.Add("subscribe"); return new ChannelInputPort<string>(subStream, weight); }
        public void UnsubscribeChannel(IInputPort port) => calls.Add("unsubscribe");
        public Task<string?> GetChannelNameAsync(int channelId) { calls.Add("name"); return Task.FromResult<string?>("channel"); }
    }
}
