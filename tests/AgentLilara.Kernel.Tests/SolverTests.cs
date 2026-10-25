using AgentLilara.Kernel.Contracts;
using AgentLilara.Kernel.Graph;
using Xunit;

namespace AgentLilara.Kernel.Tests;

public class SolverTests
{
    /// <summary>手搭一张 `preset.mention` 形状的图：被 @ 才产出 session.prompt 意图。</summary>
    private static GraphClass BuildMentionGraph()
    {
        var nodes = new INode[]
        {
            new OnMessageEvent("on"),
            new EventMention("mention"),
            new GateIf("gate"),
            new EventField("text", "text"),
            new ConstValue("session", Cell.Of("s1")),
            new IntentSink("prompt", IntentCatalog.Find("sink.prompt")!),
        };

        var links = new[]
        {
            new Link("on", "event", "mention", "event"),
            new Link("on", "event", "gate", "event"),
            new Link("mention", "result", "gate", "cond"),
            new Link("on", "event", "text", "event"),
            new Link("gate", "event", "prompt", "event"),
            new Link("session", "value", "prompt", "sessionRef"),
            new Link("text", "value", "prompt", "text"),
        };

        return new GraphClass("preset.mention", GraphKind.Static, nodes, links);
    }

    private static EvalContext NewContext(bool mentionsBot) =>
        new("trace-1", 1, InboundEvent.Message(
            "file", "chan-1", "hello",
            ChannelKind.Group,
            senderId: "u1",
            mentionsBot: mentionsBot,
            at: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void StaticGraph_EmitsIntent_WhenActivated()
    {
        var instance = new GraphInstance(BuildMentionGraph());
        var result = instance.Run(NewContext(mentionsBot: true));

        Assert.True(result.Success, string.Join(",", result.Unresolved));

        var intent = Assert.Single(instance.LastIntents);
        Assert.Equal(IntentKind.SessionPrompt, intent.Kind);
        Assert.Equal("s1", intent.Target);
        Assert.Equal("hello", intent.Args["text"].As<string>());
    }

    [Fact]
    public void StaticGraph_EvaluationOrderIsDeterministic()
    {
        var first = NewContext(mentionsBot: true);
        new GraphInstance(BuildMentionGraph()).Run(first);

        var second = NewContext(mentionsBot: true);
        new GraphInstance(BuildMentionGraph()).Run(second);

        Assert.Equal(first.EvaluationOrder, second.EvaluationOrder);
    }

    [Fact]
    public void StaticGraph_WithCycle_IsSolveInvalid_AndProducesNothing()
    {
        var nodes = new INode[]
        {
            new OnMessageEvent("on"),
            new ConstValue("c", Cell.Of(true)),
            new LogicAnd("a1"),
            new LogicAnd("a2"),
        };

        var links = new[]
        {
            new Link("c", "value", "a1", "a"),
            new Link("a2", "result", "a1", "b"),
            new Link("c", "value", "a2", "a"),
            new Link("a1", "result", "a2", "b"),
        };

        var instance = new GraphInstance(new GraphClass("cycle", GraphKind.Static, nodes, links));
        var result = instance.Run(NewContext(mentionsBot: true));

        Assert.False(result.Success);
        Assert.Equal(new[] { "a1", "a2" }, result.Unresolved);
        Assert.Empty(instance.LastIntents);
    }

    [Fact]
    public void Gate_WhenConditionFalse_DownstreamIsNoEvent_AndNoIntent()
    {
        var instance = new GraphInstance(BuildMentionGraph());
        var result = instance.Run(NewContext(mentionsBot: false));

        // 求解仍然成功：无事件是"已确定"，不是未知
        Assert.True(result.Success, string.Join(",", result.Unresolved));
        Assert.Empty(instance.LastIntents);
        Assert.Empty(instance.LastSinkInvocations); // 汇块登记了候选，但没使能
    }

    [Fact]
    public void StaticGraph_UnboundInput_ThrowsValidation()
    {
        var nodes = new INode[] { new OnMessageEvent("on"), new GateIf("gate") };
        var links = new[] { new Link("on", "event", "gate", "event") };

        var instance = new GraphInstance(new GraphClass("broken", GraphKind.Static, nodes, links));

        var ex = Assert.Throws<GraphValidationException>(() => instance.Run(NewContext(true)));
        Assert.Contains("unbound", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DynamicGraph_ReadsSnapshotWithinTick_AndFiresOnceOnRisingEdge()
    {
        var nodes = new INode[]
        {
            new OnClock("clock"),
            new ReadVariable("read", "count"),
            new ConstValue("one", Cell.Of(1)),
            new ConstValue("three", Cell.Of(3)),
            new AddInt("add"),
            new CompareGe("ge"),
            new GateIf("gate"),
            new ConstValue("key", Cell.Of("hit")),
            new IntentSink("sink", IntentCatalog.Find("sink.set")!),
            new WriteVariable("write", "count"),
        };

        var links = new[]
        {
            new Link("read", "value", "add", "a"),
            new Link("one", "value", "add", "b"),
            new Link("add", "result", "write", "value"),
            new Link("read", "value", "ge", "value"),
            new Link("three", "value", "ge", "threshold"),
            new Link("clock", "event", "gate", "event"),
            new Link("ge", "result", "gate", "cond"),
            new Link("gate", "event", "sink", "event"),
            new Link("key", "value", "sink", "key"),
            new Link("ge", "result", "sink", "value"),
        };

        var variables = new[] { new VariableDecl("count", Cell.Of(0)) };
        var instance = new GraphInstance(new GraphClass("preset.counter", GraphKind.Dynamic, nodes, links, variables));

        // 第一拍：快照读到 0，写入 1；若误读本拍新值会得到 2
        var first = instance.Run(new EvalContext("t1", 1, InboundEvent.Clock(1)));
        Assert.True(first.Success);
        Assert.Equal(1, instance.ReadVariable("count").AsInt());
        Assert.Empty(instance.LastIntents);

        instance.Run(new EvalContext("t2", 2, InboundEvent.Clock(2)));
        instance.Run(new EvalContext("t3", 3, InboundEvent.Clock(3)));
        Assert.Equal(3, instance.ReadVariable("count").AsInt());

        // 第四拍：快照读到 3，条件首次成立 → 上升沿，产出一次意图
        instance.Run(new EvalContext("t4", 4, InboundEvent.Clock(4)));
        Assert.Equal(4, instance.ReadVariable("count").AsInt());
        var intent = Assert.Single(instance.LastIntents);
        Assert.Equal("hit", intent.Target);
    }

    [Fact]
    public void Instance_VariablesArePubliclyReadable()
    {
        var nodes = new INode[] { new OnClock("clock"), new ConstValue("v", Cell.Of(7)), new WriteVariable("write", "count") };
        var links = new[] { new Link("v", "value", "write", "value") };
        var graph = new GraphClass("vars", GraphKind.Dynamic, nodes, links, new[] { new VariableDecl("count", Cell.Of(0)) });
        var instance = new GraphInstance(graph);

        instance.Run(new EvalContext("t1", 1, InboundEvent.Clock(1)));

        Assert.Equal(7, instance.Variables["count"].AsInt());
    }
}
