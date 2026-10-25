using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;
using Xunit;

namespace AgentLilara.GraphRuntime.Tests;

public class GraphLoaderTests
{
    private const string MentionSource = """
    {
      "classId": "preset.mention",
      "kind": "static",
      "version": 1,
      "nodes": [
        { "id": "on",      "type": "on.message" },
        { "id": "mention", "type": "event.mention" },
        { "id": "gate",    "type": "gate.if" },
        { "id": "text",    "type": "event.get", "params": { "field": "text" } },
        { "id": "session", "type": "read.const", "params": { "value": "s1" } },
        { "id": "prompt",  "type": "sink.prompt" }
      ],
      "links": [
        { "from": "on.event",       "to": "mention.event" },
        { "from": "on.event",       "to": "gate.event" },
        { "from": "mention.result", "to": "gate.cond" },
        { "from": "on.event",       "to": "text.event" },
        { "from": "gate.event",     "to": "prompt.event" },
        { "from": "session.value",  "to": "prompt.sessionRef" },
        { "from": "text.value",     "to": "prompt.text" }
      ]
    }
    """;

    private const string CounterSource = """
    {
      "classId": "preset.counter",
      "kind": "dynamic",
      "version": 1,
      "variables": [
        { "name": "count", "type": "int", "initial": 0, "description": "累计拍数" }
      ],
      "nodes": [
        { "id": "clock", "type": "on.clock" },
        { "id": "read",  "type": "read.variable", "params": { "name": "count" } },
        { "id": "one",   "type": "read.const", "params": { "value": 1 } },
        { "id": "three", "type": "read.const", "params": { "value": 3 } },
        { "id": "add",   "type": "math.add" },
        { "id": "ge",    "type": "compare.ge" },
        { "id": "gate",  "type": "gate.if" },
        { "id": "key",   "type": "read.const", "params": { "value": "hit" } },
        { "id": "sink",  "type": "sink.set" },
        { "id": "write", "type": "write.variable", "params": { "name": "count" } }
      ],
      "links": [
        { "from": "read.value",  "to": "add.a" },
        { "from": "one.value",   "to": "add.b" },
        { "from": "add.result",  "to": "write.value" },
        { "from": "read.value",  "to": "ge.value" },
        { "from": "three.value", "to": "ge.threshold" },
        { "from": "clock.event", "to": "gate.event" },
        { "from": "ge.result",   "to": "gate.cond" },
        { "from": "gate.event",  "to": "sink.event" },
        { "from": "key.value",   "to": "sink.key" },
        { "from": "ge.result",   "to": "sink.value" }
      ]
    }
    """;

    private static InboundEvent Message(bool mentionsBot = true) =>
        InboundEvent.Message("file", "chan-1", "hello", senderId: "u1", mentionsBot: mentionsBot);

    [Fact]
    public void LoadMention_EmitsSessionPromptIntent()
    {
        var graph = GraphLoader.Load(MentionSource);
        Assert.Equal(GraphKind.Static, graph.Kind);
        Assert.Equal(6, graph.Nodes.Count);

        var instance = new GraphInstance(graph);
        var result = instance.Run(new EvalContext("t1", 1, Message()));

        Assert.True(result.Success, string.Join(",", result.Unresolved));
        var intent = Assert.Single(instance.LastIntents);
        Assert.Equal(IntentKind.SessionPrompt, intent.Kind);
        Assert.Equal("s1", intent.Target);
        Assert.Equal("hello", intent.Args["text"].As<string>());
    }

    [Fact]
    public void LoadCounter_UsesSnapshotAndFiresOnRisingEdge()
    {
        var graph = GraphLoader.Load(CounterSource);
        Assert.Equal(GraphKind.Dynamic, graph.Kind);
        Assert.Single(graph.Variables);

        var instance = new GraphInstance(graph);
        instance.Run(new EvalContext("t1", 1, InboundEvent.Clock(1)));

        // 快照读到 0，写入 1（若误读本拍新值会得到 2）
        Assert.Equal(1, instance.ReadVariable("count").AsInt());
        Assert.Empty(instance.LastIntents);

        instance.Run(new EvalContext("t2", 2, InboundEvent.Clock(2)));
        instance.Run(new EvalContext("t3", 3, InboundEvent.Clock(3)));
        instance.Run(new EvalContext("t4", 4, InboundEvent.Clock(4)));

        Assert.Equal(4, instance.ReadVariable("count").AsInt());
        Assert.Single(instance.LastIntents);
    }

    [Fact]
    public void LoadTwice_ProducesSameEvaluationOrder()
    {
        var firstContext = new EvalContext("a", 1, Message());
        new GraphInstance(GraphLoader.Load(MentionSource)).Run(firstContext);

        var secondContext = new EvalContext("b", 1, Message());
        new GraphInstance(GraphLoader.Load(MentionSource)).Run(secondContext);

        Assert.Equal(firstContext.EvaluationOrder, secondContext.EvaluationOrder);
    }

    [Fact]
    public void UnknownNodeType_Throws()
    {
        var json = MentionSource.Replace("\"type\": \"sink.prompt\"", "\"type\": \"sink.teleport\"", StringComparison.Ordinal);
        var ex = Assert.Throws<GraphLoadException>(() => GraphLoader.Load(json));
        Assert.Contains("unknown type", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEventField_Throws()
    {
        var json = MentionSource.Replace("\"field\": \"text\"", "\"field\": \"txt\"", StringComparison.Ordinal);
        var ex = Assert.Throws<GraphLoadException>(() => GraphLoader.Load(json));
        Assert.Contains("unknown event field", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownParam_Throws()
    {
        var json = MentionSource.Replace("\"value\": \"s1\"", "\"values\": \"s1\"", StringComparison.Ordinal);
        var ex = Assert.Throws<GraphLoadException>(() => GraphLoader.Load(json));
        Assert.Contains("unknown param", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateNodeId_Throws()
    {
        var json = MentionSource.Replace(
            "{ \"id\": \"prompt\",  \"type\": \"sink.prompt\" }",
            "{ \"id\": \"gate\",    \"type\": \"sink.prompt\" }",
            StringComparison.Ordinal);
        var ex = Assert.Throws<GraphValidationException>(() => GraphLoader.Load(json));
        Assert.Contains("duplicate node id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingActivationSource_Throws()
    {
        const string json = """
        {
          "classId": "no.source",
          "kind": "static",
          "nodes": [
            { "id": "one", "type": "read.const", "params": { "value": 1 } },
            { "id": "set", "type": "sink.set" }
          ],
          "links": []
        }
        """;

        var ex = Assert.Throws<GraphValidationException>(() => GraphLoader.Load(json));
        Assert.Contains("exactly one activation source", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedLink_Throws()
    {
        var json = MentionSource.Replace("\"from\": \"session.value\"", "\"from\": \"sessionvalue\"", StringComparison.Ordinal);
        var ex = Assert.Throws<GraphLoadException>(() => GraphLoader.Load(json));
        Assert.Contains("node.port", ex.Message, StringComparison.Ordinal);
    }
}
