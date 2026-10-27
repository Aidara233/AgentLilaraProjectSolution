using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.WebUI.Providers;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SessionDiagnosticsTests
{
    [Fact]
    public void RuntimeDiagnosticsReuseExistingRoundCounter()
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        session.InheritRoundCount(23);
        Assert.Equal(23, ((IRuntimeSessionDiagnostics)session).RoundCount);
        Assert.Equal(23, session.RoundCountInternal);
        Assert.Equal(23, SessionsProvider.GetRoundCount(session));
        session.InheritRoundCount(41);
        Assert.Equal(41, SessionsProvider.GetRoundCount(session));
        Assert.Equal(41, session.BuildStateSnapshot().RoundCount);
    }

    [Fact]
    public void SessionWithoutOptionalDiagnosticsKeepsZeroFallback()
    {
        Assert.Equal(0, SessionsProvider.GetRoundCount(new Session()));
    }

    private sealed class Session : IRuntimeSession
    {
        public string SessionId => "custom";
        public SessionStatus Status => SessionStatus.Creating;
        public bool IsAlive => false;
        public IReadOnlyList<string> ComponentIds => Array.Empty<string>();
        public IReadOnlyList<string> ActiveTools => Array.Empty<string>();
        public IReadOnlyDictionary<string, IInputPort> Inputs => new Dictionary<string, IInputPort>();
        public IReadOnlyDictionary<string, IOutputPort> Outputs => new Dictionary<string, IOutputPort>();
        public string? ParentSessionId => null;
        public string? EffectiveParentId => null;
        public IReadOnlyList<string> ChildSessionIds => Array.Empty<string>();
        public DateTime? WaitUntil => null;
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
        public void RequestStop(string reason) { }
        public Task PersistStateAsync() => Task.CompletedTask;
        public void EnableComponent(string id) { }
        public void DisableComponent(string id) { }
    }
}
