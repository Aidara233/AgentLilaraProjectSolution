// AgentCoreProcessor.Tests/SessionResourceRegistryTests.cs
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

public class SessionResourceRegistryTests
{
    [Fact]
    public void Register_then_ResolveActiveSession_returns_same_id_when_alive()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);
        var resource = new FakeResource();
        engine.RegisterSessionResource(resource);

        var resolved = engine.ResolveActiveSession("S1");
        Assert.Equal("S1", resolved);
    }

    [Fact]
    public void ResolveActiveSession_follows_forward_chain()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S2", alive: true);
        engine.SeedForward("S1", "S2"); // S1 compacted to S2
        var resolved = engine.ResolveActiveSession("S1");
        Assert.Equal("S2", resolved);
    }

    [Fact]
    public void ResolveActiveSession_returns_null_when_session_dead_and_not_forwarded()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("SX", alive: false);
        var resolved = engine.ResolveActiveSession("SX");
        Assert.Null(resolved);
    }

    [Fact]
    public void ResolveActiveSession_unknown_reference_returns_null()
    {
        // LoopId 前缀概念已移除：未知引用（如 "channel:5"）直接 null，不再原样返回
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);
        var resolved = engine.ResolveActiveSession("channel:5");
        Assert.Null(resolved);
    }

    [Fact]
    public void WakeUpSession_follows_forward_to_active_session()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S2", alive: true);
        engine.SeedForward("S1", "S2");
        var resource = new FakeResource();
        engine.RegisterSessionResource(resource);

        engine.WakeUpSession("S1"); // should wake S2
        Assert.True(engine.WasWoken("S2"));
    }

    [Fact]
    public async Task CreateFromCompact_notifies_resources_to_rebind()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);
        var resource = new FakeResource();
        engine.RegisterSessionResource(resource);
        await engine.NotifyResourcesCompactingAsync("S1", "S2");
        Assert.Equal("S1", resource.LastRebindOld);
        Assert.Equal("S2", resource.LastRebindNew);
    }

    [Fact]
    public void StopSession_notifies_resources_freed()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);
        var resource = new FakeResource();
        engine.RegisterSessionResource(resource);
        engine.StopSession("S1", "test");
        Assert.Equal("S1", resource.LastFreed);
    }

    private class FakeResource : ISessionResource
    {
        public string? LastRebindOld;
        public string? LastRebindNew;
        public string? LastFreed;
        public Task OnSessionCompactingAsync(string oldSessionId, string newSessionId)
        { LastRebindOld = oldSessionId; LastRebindNew = newSessionId; return Task.CompletedTask; }
        public Task OnSessionFreedAsync(string sessionId)
        { LastFreed = sessionId; return Task.CompletedTask; }
        public bool HasActiveResources(string sessionId) => false;
    }
}
