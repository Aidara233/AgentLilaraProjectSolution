using System.Reflection;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Models;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SessionModelPortTests
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static IAgentModel Active(RuntimeSession session) => (IAgentModel)typeof(RuntimeSession).GetField("_agentCore", Private)!.GetValue(session)!;
    private static Task<ModelOutput?> Invoke(RuntimeSession session, IAgentModel model, AgentInvokeRequest request) =>
        (Task<ModelOutput?>)typeof(RuntimeSession).GetMethod("InvokeModelAsync", Private)!.Invoke(session, new object[] { model, request, 1 })!;

    [Fact]
    public void FactoryIsRequiredAndCreatesOnlyWhitelistedLazyCachedModels()
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = "task" }, "test-session", new[] { "basic-tools" });
        Assert.Throws<InvalidOperationException>(() => session.ConfigureCores(new[] { "A", "B" }, "A"));
        var bindings = new List<AgentModelBinding>();
        session.SetModelFactory(binding => { bindings.Add(binding); return new FakeModel(); });
        typeof(RuntimeSession).GetField("_sessionPrompt", Private)!.SetValue(session, "identity");
        session.ConfigureCores(new[] { "A", "B" }, "A", "B");
        var first = Assert.IsType<FakeModel>(Active(session));
        Assert.Equal("identity", first.SessionPrompt);
        Assert.Equal(new AgentModelBinding("B", "test-session", "task"), Assert.Single(bindings));
        session.SetActiveCore("unknown");
        Assert.Same(first, Active(session));
        session.SetActiveCore("A");
        var second = Assert.IsType<FakeModel>(Active(session));
        Assert.NotSame(first, second);
        Assert.Equal("identity", second.SessionPrompt);
        session.SetActiveCore("b");
        Assert.Same(first, Active(session));
        Assert.Equal(2, bindings.Count);
        Assert.Throws<InvalidOperationException>(() => session.SetModelFactory(_ => new FakeModel()));
        Assert.Equal("Lilara.Turn", typeof(IAgentModel).Assembly.GetName().Name);
        Assert.Same(typeof(IAgentModel).Assembly, typeof(AgentInvokeRequest).Assembly);
    }

    [Fact]
    public async Task InFlightCallKeepsPairedModelWhenActiveModelChanges()
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<ModelOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeModel { UseNativeTools = true, DisableImageMessages = true,
            Call = (_, _) => { entered.SetResult(); return completion.Task; } };
        var second = new FakeModel();
        session.SetModelFactory(binding => binding.ConfigurationName == "A" ? first : second);
        typeof(RuntimeSession).GetField("_sessionPrompt", Private)!.SetValue(session, "identity");
        session.ConfigureCores(new[] { "A", "B" }, "A");
        var request = new AgentInvokeRequest("channel", Array.Empty<ITool>());
        var round = new RoundContext(request, Active(session));
        var pending = Invoke(session, round.Core, round.Request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.SetActiveCore("B");
        completion.SetResult(ModelOutput.FromText("first"));
        Assert.Equal("first", (await pending)!.Value.Text);
        Assert.Same(session.History, first.Messages);
        Assert.Same(request, first.Request);
        Assert.Same(first, round.Core);
        Assert.True(round.Core.UseNativeTools);
        Assert.True(round.Core.DisableImageMessages);
        Assert.Same(second, Active(session));
        Assert.Null(second.Request);
        Assert.Equal("fake", (await Invoke(session, Active(session), request))!.Value.Text);
        Assert.Same(request, second.Request);
    }

    [Fact]
    public async Task ModelFailureAddsErrorAndAllowsNextCall()
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        var model = new FakeModel { Call = (_, _) => Task.FromException<ModelOutput>(new InvalidOperationException("controlled")) };
        var request = new AgentInvokeRequest("channel", Array.Empty<ITool>());
        Assert.Null(await Invoke(session, model, request));
        Assert.Contains(session.History, message => message.Role == "system" && message.Content.Contains("controlled"));
        model.Call = (_, _) => Task.FromResult(ModelOutput.FromText("recovered"));
        Assert.Equal("recovered", (await Invoke(session, model, request))!.Value.Text);
    }

    private sealed class FakeModel : IAgentModel
    {
        public bool UseNativeTools { get; init; }
        public bool DisableImageMessages { get; init; }
        public string? SessionPrompt { get; set; }
        public List<Message>? Messages;
        public AgentInvokeRequest? Request;
        public Func<List<Message>, AgentInvokeRequest, Task<ModelOutput>> Call = (_, _) => Task.FromResult(ModelOutput.FromText("fake"));
        public void ConfigureDefaults(string engineType, IReadOnlyList<ITool> tools) { }
        public Task<ModelOutput> InvokeAsync(List<Message> messages, AgentInvokeRequest request)
        { Messages = messages; Request = request; return Call(messages, request); }
    }
}
