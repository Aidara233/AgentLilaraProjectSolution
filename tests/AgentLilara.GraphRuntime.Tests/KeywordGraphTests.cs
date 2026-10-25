using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;
using Xunit;

namespace AgentLilara.GraphRuntime.Tests;

public sealed class KeywordGraphTests
{
    private const string Json = """
    {
      "classId": "preset.keyword-test",
      "kind": "static",
      "version": 1,
      "nodes": [
        { "id": "on",       "type": "on.message" },
        { "id": "text",     "type": "event.get", "params": { "field": "text" } },
        { "id": "needle",   "type": "read.const", "params": { "value": "测试" } },
        { "id": "contains", "type": "text.contains" },
        { "id": "gate",     "type": "gate.if" },
        { "id": "reply",    "type": "read.const", "params": { "value": "这是测试回复" } },
        { "id": "send",     "type": "sink.send" }
      ],
      "links": [
        { "from": "on.event",        "to": "text.event" },
        { "from": "text.value",      "to": "contains.text" },
        { "from": "needle.value",    "to": "contains.needle" },
        { "from": "on.event",        "to": "gate.event" },
        { "from": "contains.result",  "to": "gate.cond" },
        { "from": "gate.event",      "to": "send.event" },
        { "from": "reply.value",     "to": "send.text" }
      ]
    }
    """;

    private static EvalContext Context(string text) => new(
        "keyword-test",
        1,
        InboundEvent.Message("test", "channel-1", text));

    [Fact]
    public void ContainsKeyword_EmitsSendIntent()
    {
        var instance = new GraphInstance(GraphLoader.Load(Json));
        var result = instance.Run(Context("这是一次测试消息"));

        Assert.True(result.Success, string.Join(",", result.Unresolved));
        var intent = Assert.Single(instance.LastIntents);
        Assert.Equal(IntentKind.SendMessage, intent.Kind);
        Assert.Equal("channel-1", intent.Target);
        Assert.Equal("这是测试回复", intent.Args["text"].As<string>());
    }

    [Fact]
    public void WithoutKeyword_EmitsNothing()
    {
        var instance = new GraphInstance(GraphLoader.Load(Json));
        var result = instance.Run(Context("普通消息"));

        Assert.True(result.Success, string.Join(",", result.Unresolved));
        Assert.Empty(instance.LastIntents);
    }
}
