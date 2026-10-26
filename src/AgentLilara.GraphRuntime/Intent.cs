namespace AgentLilara.GraphRuntime.Graph;

/// <summary>
/// 动作种类。这是**动作接口集**的枚举——有限、只增不改、每个都对应一个节点类型（见 <see cref="IntentCatalog"/>）。
/// 图不执行动作，只产出意图；执行归骨架（内核）。
/// </summary>
public enum IntentKind
{
    /// <summary>发言（骨架负责出向排队、台账、重试、真正发送）。</summary>
    SendMessage,

    /// <summary>驱动既有会话（骨架负责起会话/续会话/预算）。</summary>
    SessionPrompt,

    /// <summary>往会话上下文注入一条消息（不激活循环）。</summary>
    SessionInject,

    /// <summary>把当前频道消息路由到频道会话；无活跃会话时请求按类型创建。</summary>
    SessionRoute,

    /// <summary>写实例/全局状态。</summary>
    StateSet,

    /// <summary>
    /// 通知这次激活所属的订阅宿主（"你在等的东西来了"）。
    /// <para>⚠️ 图不填参数、不填目标：投递到哪个频道、文本怎么渲染，全由骨架按**订阅记录**决定。
    /// 图只回答"条件命中了没有"——它不该知道会话 id，因为会话可能还没建。</para>
    /// </summary>
    Notify,

    MemoryRemember,
    ToolCall,
    GraphPulse,
}

/// <summary>
/// 图产出的一次动作意图。求解无效时不产生任何意图（零副作用）。
/// </summary>
public sealed record ActionIntent(IntentKind Kind, string Target, IReadOnlyDictionary<string, Cell> Args);

/// <summary>
/// 一个动作节点类型的规格：节点类型 ↔ 动作种类 ↔ 输入口（除 <c>event</c>）↔ 哪个口当 Target。
/// </summary>
public sealed record IntentSpec(
    string NodeType,
    IntentKind Kind,
    IReadOnlyList<string> ArgPorts,
    string? TargetArg);

/// <summary>
/// 动作接口清单在代码里的唯一落点。加动作 = 在这里加一行 + 写进 `15-wiring-contract.md`。
/// </summary>
public static class IntentCatalog
{
    public static IReadOnlyList<IntentSpec> All { get; } = new[]
    {
        new IntentSpec("sink.send", IntentKind.SendMessage, new[] { "text" }, TargetArg: null),
        new IntentSpec("sink.prompt", IntentKind.SessionPrompt, new[] { "sessionRef", "text" }, TargetArg: "sessionRef"),
        new IntentSpec("sink.inject", IntentKind.SessionInject, new[] { "sessionRef", "text" }, TargetArg: "sessionRef"),
        new IntentSpec("sink.route", IntentKind.SessionRoute, new[] { "sessionType" }, TargetArg: "sessionType"),
        new IntentSpec("sink.set", IntentKind.StateSet, new[] { "key", "value" }, TargetArg: "key"),

        // 无参数口：命中即通知，投递目标与文本由骨架按订阅记录决定（图不知道会话 id）。
        new IntentSpec("sink.notify", IntentKind.Notify, Array.Empty<string>(), TargetArg: null),
    };

    public static IntentSpec? Find(string nodeType) =>
        All.FirstOrDefault(spec => string.Equals(spec.NodeType, nodeType, StringComparison.Ordinal));

    public static IReadOnlyList<string> NodeTypes => All.Select(spec => spec.NodeType).ToList();
}

/// <summary>汇块能在阶段③产出意图（派发型动作）。</summary>
public interface IIntentSink
{
    void Emit(IReadOnlyDictionary<string, Cell> args, EvalContext context);
}
