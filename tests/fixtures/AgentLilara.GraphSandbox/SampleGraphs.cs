using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;

namespace AgentLilara.GraphSandbox;

/// <summary>沙箱内置的预制图与样例（对外文档：`15-wiring-contract.md`）。</summary>
public static class SampleGraphs
{
    /// <summary>
    /// 预制图 `preset.mention`：群内被 @ 才响应，产出一条 <c>session.prompt</c> 意图。
    /// 这是首启默认装的那张——"图只负责第③层（要不要响应、怎么响应）"。
    /// </summary>
    public const string PresetMentionJson = """
    {
      "classId": "preset.mention",
      "kind": "static",
      "version": 1,
      "nodes": [
        { "id": "on",      "type": "on.message" },
        { "id": "mention", "type": "event.mention" },
        { "id": "gate",    "type": "gate.if" },
        { "id": "text",    "type": "event.get", "params": { "field": "text" } },
        { "id": "session", "type": "read.const", "params": { "value": "default" } },
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

    /// <summary>
    /// 预制图 `preset.counter`：动态图样例（时钟沿 + 变量快照 + 上升沿），
    /// 计数越过阈值那一拍产出一次 <c>state.set</c> 意图。归档保留，`preset.digest` 会用到。
    /// </summary>
    public const string PresetCounterJson = """
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

    /// <summary>样例：把消息交给 agent 模块（需要模块宿主：<c>GraphLoader.Load(AgentCallJson, host.Registry)</c>）。</summary>
    public const string AgentCallJson = """
    {
      "classId": "channel.agent",
      "kind": "static",
      "version": 1,
      "nodes": [
        { "id": "on",      "type": "on.message" },
        { "id": "mention", "type": "event.mention" },
        { "id": "gate",    "type": "gate.if" },
        { "id": "text",    "type": "event.get", "params": { "field": "text" } },
        { "id": "session", "type": "read.const", "params": { "value": "file-session" } },
        { "id": "agent",   "type": "module.agent.loop.run" }
      ],
      "links": [
        { "from": "on.event",       "to": "mention.event" },
        { "from": "on.event",       "to": "gate.event" },
        { "from": "mention.result", "to": "gate.cond" },
        { "from": "on.event",       "to": "text.event" },
        { "from": "gate.event",     "to": "agent.event" },
        { "from": "session.value",  "to": "agent.sessionRef" },
        { "from": "text.value",     "to": "agent.text" }
      ]
    }
    """;

    public static GraphClass LoadPresetMention() => GraphLoader.Load(PresetMentionJson);

    /// <summary>
    /// 预制图 `preset.activation`：**从旧代码拆出来的真实逻辑**——"这条消息要不要激活会话"。
    /// 旧规则见 `RuntimeSession.WaitForActivationAsync`（mention 立即激活 / 普通消息累积 20 条激活 / 激活后清零）。
    /// 这里用一张静态图 + 一个变量表达同样语义；`logic.select` 保证 `pending` 只有一个写入点。
    /// </summary>
    public const string PresetActivationJson = """
    {
      "classId": "preset.activation",
      "kind": "static",
      "version": 1,
      "variables": [
        { "name": "pending", "type": "int", "initial": 0, "description": "普通消息累积条数" }
      ],
      "nodes": [
        { "id": "on",      "type": "on.message" },
        { "id": "mention", "type": "event.mention" },
        { "id": "read",    "type": "read.variable", "params": { "name": "pending" } },
        { "id": "one",     "type": "read.const", "params": { "value": 1 } },
        { "id": "add",     "type": "math.add" },
        { "id": "limit",   "type": "read.const", "params": { "value": 20 } },
        { "id": "ge",      "type": "compare.ge" },
        { "id": "any",     "type": "logic.or" },
        { "id": "zero",    "type": "read.const", "params": { "value": 0 } },
        { "id": "next",    "type": "logic.select" },
        { "id": "write",   "type": "write.variable", "params": { "name": "pending" } },
        { "id": "text",    "type": "event.get", "params": { "field": "text" } },
        { "id": "session", "type": "read.const", "params": { "value": "default" } },
        { "id": "gate",    "type": "gate.if" },
        { "id": "prompt",  "type": "sink.prompt" }
      ],
      "links": [
        { "from": "on.event",       "to": "mention.event" },
        { "from": "read.value",     "to": "add.a" },
        { "from": "one.value",      "to": "add.b" },
        { "from": "add.result",     "to": "ge.value" },
        { "from": "limit.value",    "to": "ge.threshold" },
        { "from": "mention.result", "to": "any.a" },
        { "from": "ge.result",      "to": "any.b" },
        { "from": "any.result",     "to": "next.cond" },
        { "from": "zero.value",     "to": "next.a" },
        { "from": "add.result",     "to": "next.b" },
        { "from": "next.result",    "to": "write.value" },
        { "from": "on.event",       "to": "gate.event" },
        { "from": "any.result",     "to": "gate.cond" },
        { "from": "on.event",       "to": "text.event" },
        { "from": "gate.event",     "to": "prompt.event" },
        { "from": "session.value",  "to": "prompt.sessionRef" },
        { "from": "text.value",     "to": "prompt.text" }
      ]
    }
    """;

    public static GraphClass LoadPresetCounter() => GraphLoader.Load(PresetCounterJson);

    public static GraphClass LoadPresetActivation() => GraphLoader.Load(PresetActivationJson);

    public static GraphClass LoadAgentCall() => GraphLoader.Load(AgentCallJson);
}
