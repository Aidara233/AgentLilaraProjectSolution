using System.Reflection;
using System.Runtime.CompilerServices;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SessionExitQueryTests
{
    private sealed class Query(List<string> calls, bool blocked) : ISessionExitQuery
    {
        public IReadOnlyCollection<string>? GetActiveChildren(string id)
        { calls.Add("children:" + id); return new[] { "child-a", "child-b" }; }
        public bool AnyResourceBlocksExit(string id) { calls.Add("resources:" + id); return blocked; }
    }

    private sealed class ExitComponent(List<string> calls) : LoopComponentBase
    {
        public override ComponentMeta Meta => new() { Name = "exit-test", Description = "test" };
        public override IEnumerable<ITool> Tools => Array.Empty<ITool>();
        public override Task<bool> OnCanExitAsync() { calls.Add("component"); return Task.FromResult(false); }
    }

    private static Task<ToolEffectContext> Facts(RuntimeSession session) =>
        (Task<ToolEffectContext>)typeof(RuntimeSession).GetMethod("BuildToolEffectContextAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, null)!;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QueriesChildrenThenResourcesAndOnlyAsksComponentWhenResourcesAllow(bool blocked)
    {
        var calls = new List<string>();
        var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        session.SetSessionExitQuery(new Query(calls, blocked));
        var host = new ComponentHost(session.SessionId, "channel", null!, null!, () => { });
        // QueryCanExitAsync only reads IsEnabled; avoid initializing plugin storage or global registries.
        var context = (LoopComponentContext)RuntimeHelpers.GetUninitializedObject(typeof(LoopComponentContext));
        context.SetEnabledDirect(true);
        var instances = (List<LoopComponentInstance>)typeof(ComponentHost)
            .GetField("_loopComponents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        instances.Add(new(new ExitComponent(calls), context, null!));
        typeof(RuntimeSession).GetField("_componentHost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, host);

        var facts = await Facts(session);
        Assert.Equal("channel", facts.SessionType);
        Assert.Equal(2, facts.ActiveChildCount);
        Assert.Equal(blocked, facts.InFlightResources);
        Assert.Equal(blocked, facts.CanExit);
        Assert.True(facts.BlocksExit);
        Assert.Equal(0, facts.ForceExitAttempts);
        var expected = new List<string> { "children:" + session.SessionId, "resources:" + session.SessionId };
        if (!blocked) expected.Add("component");
        Assert.Equal(expected, calls);
    }

    [Fact]
    public async Task NoQueryOrComponentPreservesDefaultFacts()
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = "task" }, new[] { "basic-tools" });
        var facts = await Facts(session);
        Assert.Equal("task", facts.SessionType);
        Assert.Equal(0, facts.ActiveChildCount);
        Assert.False(facts.InFlightResources);
        Assert.True(facts.CanExit);
        Assert.False(facts.BlocksExit);
    }
}
