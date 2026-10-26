using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 外部信源事件（P1）：事件种类隔离、载荷袋的三值语义、精确匹配、挂载参数默认值、sink.notify。
/// <para>这些是"框架承担机械行为"的地基——判定必须能在不唤醒模型的情况下做完，
/// 所以任何"该拦没拦住"都是成本泄漏，任何"该放没放行"都是功能失效。</para>
/// </summary>
public class GraphExternalEventTests
{
    // ── 事件种类隔离 ──

    [Fact]
    public void OnMessageIgnoresExternalEvent()
    {
        // on.message 只认 MessageReceived：邮件绝不能伪装成聊天消息进频道图。
        var graph = Load("""
        {
          "classId": "test.onmessage",
          "kind": "static",
          "nodes": [
            { "id": "on", "type": "on.message" },
            { "id": "notify", "type": "sink.notify" }
          ],
          "links": [ { "from": "on.event", "to": "notify.event" } ]
        }
        """);

        var intents = Run(graph, External(from: "boss@corp"), mountParameters: null);

        Assert.Empty(intents);
    }

    [Fact]
    public void OnExternalIgnoresMessageEvent()
    {
        // 反向隔离：外部事件源不吃聊天消息。
        var graph = Load("""
        {
          "classId": "test.onexternal",
          "kind": "static",
          "nodes": [
            { "id": "on", "type": "on.external" },
            { "id": "notify", "type": "sink.notify" }
          ],
          "links": [ { "from": "on.event", "to": "notify.event" } ]
        }
        """);

        var message = InboundEvent.Message("test", "42", "hello", ChannelKind.Group);
        var intents = Run(graph, message, mountParameters: null);

        Assert.Empty(intents);
    }

    [Fact]
    public void OnExternalEmitsNotifyWithoutArguments()
    {
        var graph = Load("""
        {
          "classId": "test.onexternal2",
          "kind": "static",
          "nodes": [
            { "id": "on", "type": "on.external" },
            { "id": "notify", "type": "sink.notify" }
          ],
          "links": [ { "from": "on.event", "to": "notify.event" } ]
        }
        """);

        var intents = Run(graph, External(from: "boss@corp"), mountParameters: null);

        var intent = Assert.Single(intents);
        Assert.Equal(IntentKind.Notify, intent.Kind);
        // 图不填目标/参数：外部事件没有频道，投递目标与文本由订阅记录与骨架决定。
        // Args 里只有求解器喂进来的触发口 event，没有任何参数口。
        Assert.Equal(string.Empty, intent.Target);
        Assert.Equal(new[] { "event" }, intent.Args.Keys.ToArray());
    }

    // ── 收录条件：来自某人 ──

    [Fact]
    public void WatchGraphNotifiesOnlyWhenSenderMatchesMountParameter()
    {
        var graph = Load(WatchGraph("test.watch"));

        var hit = Run(graph, External(from: "boss@corp"), Params(("from", "boss@corp")));
        Assert.Single(hit);

        var miss = Run(graph, External(from: "spam@corp"), Params(("from", "boss@corp")));
        Assert.Empty(miss);
    }

    [Fact]
    public void WatchGraphFailsClosedWhenSenderParameterIsUnset()
    {
        // 未配置 from 且没有默认值 → read.mount 给 unknown → compare.eq 未知 → 闸门不放行。
        var graph = Load(WatchGraph("test.watch.noparam"));

        var intents = Run(graph, External(from: "boss@corp"), mountParameters: null);

        Assert.Empty(intents);
    }

    [Fact]
    public void MissingExtKeyIsUnknownNotEmptyString()
    {
        // 信源没给 from 字段：不能当成"发件人为空"而误命中。
        var graph = Load(WatchGraph("test.watch.missingext"));

        var evt = InboundEvent.External("mail", "body", senderId: null, ext: null);
        var intents = Run(graph, evt, Params(("from", string.Empty)));

        Assert.Empty(intents);
    }

    [Fact]
    public void ReadMountDefaultMakesConditionOptional()
    {
        // 配 default:"" + text.contains：空针命中任意文本，于是"没配 = 不过滤"。
        var graph = Load("""
        {
          "classId": "test.optional",
          "kind": "static",
          "nodes": [
            { "id": "on", "type": "on.external" },
            { "id": "subject", "type": "event.ext", "params": { "name": "subject" } },
            { "id": "want", "type": "read.mount", "params": { "name": "subject", "default": "" } },
            { "id": "has", "type": "text.contains" },
            { "id": "gate", "type": "gate.if" },
            { "id": "notify", "type": "sink.notify" }
          ],
          "links": [
            { "from": "on.event", "to": "subject.event" },
            { "from": "subject.value", "to": "has.text" },
            { "from": "want.value", "to": "has.needle" },
            { "from": "on.event", "to": "gate.event" },
            { "from": "has.result", "to": "gate.cond" },
            { "from": "gate.event", "to": "notify.event" }
          ]
        }
        """);

        // 没配 subject 参数 → 一律放行
        Assert.Single(Run(graph, External(ext: Subject("周报")), mountParameters: null));

        // 配了 subject → 只有包含才放行
        Assert.Single(Run(graph, External(ext: Subject("本周周报")), Params(("subject", "周报"))));
        Assert.Empty(Run(graph, External(ext: Subject("广告")), Params(("subject", "周报"))));
    }

    // ── compare.eq 语义 ──

    [Theory]
    [InlineData("Boss@Corp", "boss@corp", false)] // 序数比较，区分大小写
    [InlineData("boss@corp", "boss@corp", true)]
    [InlineData("boss@corp", "boss@corp2", false)]
    public void CompareEqUsesOrdinalComparison(string a, string b, bool expected)
    {
        var graph = Load("""
        {
          "classId": "test.eq",
          "kind": "static",
          "nodes": [
            { "id": "on", "type": "on.external" },
            { "id": "a", "type": "read.const", "params": { "value": "A" } },
            { "id": "b", "type": "read.const", "params": { "value": "B" } },
            { "id": "eq", "type": "compare.eq" },
            { "id": "gate", "type": "gate.if" },
            { "id": "notify", "type": "sink.notify" }
          ],
          "links": [
            { "from": "a.value", "to": "eq.a" },
            { "from": "b.value", "to": "eq.b" },
            { "from": "on.event", "to": "gate.event" },
            { "from": "eq.result", "to": "gate.cond" },
            { "from": "gate.event", "to": "notify.event" }
          ]
        }
        """);

        // read.const 的 value 是加载期参数，这里按用例替换成实际文本
        var intents = Run(Load(EqGraph(a, b)), External(), mountParameters: null);

        Assert.Equal(expected, intents.Count == 1);
    }

    // ── 加载期校验 ──

    [Fact]
    public void OnExternalCountsAsTheSingleActivationSource()
    {
        var ex = Assert.Throws<GraphValidationException>(() => Load("""
        {
          "classId": "test.twosource",
          "kind": "static",
          "nodes": [
            { "id": "on", "type": "on.external" },
            { "id": "on2", "type": "on.message" },
            { "id": "notify", "type": "sink.notify" }
          ],
          "links": [ { "from": "on.event", "to": "notify.event" } ]
        }
        """));

        Assert.Contains("activation source", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── 帮手 ──

    private static string WatchGraph(string classId) => $$"""
    {
      "classId": "{{classId}}",
      "kind": "static",
      "nodes": [
        { "id": "on", "type": "on.external" },
        { "id": "from", "type": "event.ext", "params": { "name": "from" } },
        { "id": "want", "type": "read.mount", "params": { "name": "from" } },
        { "id": "eq", "type": "compare.eq" },
        { "id": "gate", "type": "gate.if" },
        { "id": "notify", "type": "sink.notify" }
      ],
      "links": [
        { "from": "on.event", "to": "from.event" },
        { "from": "from.value", "to": "eq.a" },
        { "from": "want.value", "to": "eq.b" },
        { "from": "on.event", "to": "gate.event" },
        { "from": "eq.result", "to": "gate.cond" },
        { "from": "gate.event", "to": "notify.event" }
      ]
    }
    """;

    /// <summary>把 compare.eq 用例的两个常量写成图（read.const 只吃加载期参数）。</summary>
    private static string EqGraph(string a, string b) => $$"""
    {
      "classId": "test.eq.case",
      "kind": "static",
      "nodes": [
        { "id": "on", "type": "on.external" },
        { "id": "a", "type": "read.const", "params": { "value": "{{a}}" } },
        { "id": "b", "type": "read.const", "params": { "value": "{{b}}" } },
        { "id": "eq", "type": "compare.eq" },
        { "id": "gate", "type": "gate.if" },
        { "id": "notify", "type": "sink.notify" }
      ],
      "links": [
        { "from": "a.value", "to": "eq.a" },
        { "from": "b.value", "to": "eq.b" },
        { "from": "on.event", "to": "gate.event" },
        { "from": "eq.result", "to": "gate.cond" },
        { "from": "gate.event", "to": "notify.event" }
      ]
    }
    """;

    private static GraphClass Load(string json) => GraphLoader.Load(json);

    private static InboundEvent External(
        string? from = null,
        IReadOnlyDictionary<string, string>? ext = null)
    {
        // 邮件的发件人同时出现在 SenderId 和 ext["from"]：前者是通用字段，后者是信源载荷。
        var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (ext is not null)
        {
            foreach (var pair in ext) bag[pair.Key] = pair.Value;
        }

        if (from is not null) bag["from"] = from;

        return InboundEvent.External(
            sourceId: "mail",
            text: "正文",
            senderId: from,
            ext: bag.Count == 0 ? null : bag,
            dedupeKey: "mail:" + Guid.NewGuid().ToString("N"));
    }

    private static IReadOnlyDictionary<string, string> Subject(string subject) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["subject"] = subject };

    private static IReadOnlyDictionary<string, string> Params(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<ActionIntent> Run(
        GraphClass graph,
        InboundEvent @event,
        IReadOnlyDictionary<string, string>? mountParameters)
    {
        var instance = new GraphInstance(graph, "test");
        var context = new EvalContext("trace:test", 1, @event)
        {
            MountParameters = mountParameters
                              ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        var result = instance.Run(context);
        Assert.True(result.Success, "solve failed: " + string.Join(", ", result.Unresolved ?? Array.Empty<string>()));

        return instance.LastIntents;
    }
}
