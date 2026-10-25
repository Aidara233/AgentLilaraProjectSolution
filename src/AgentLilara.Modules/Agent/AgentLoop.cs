using AgentLilara.Kernel.Modules;

namespace AgentLilara.Modules.Agent;

/// <summary>
/// 带工具循环的 Agent（`00-decisions.md` D56：agent 循环整体是一个模块）。
/// 图只看到"起一次会话 / 取消"，内部多轮模型调用与工具执行不可见。
///
/// 配置走**属性**而不是大 record 参数：图用 <c>write.agent.loop.model</c> 这些节点设置，
/// 方法只接收这一条消息真正需要的东西。
/// </summary>
[Module("agent.loop", Version = "0.1.0", RequiredCapabilities = new[] { Capabilities.Net })]
public interface IAgentLoop
{
    /// <summary>实例就绪：加载模型配置、准备工具表。</summary>
    [Lifecycle(LifecycleAttribute.Mount)]
    Task OnMountAsync();

    /// <summary>实例销毁前：中止在跑的会话。</summary>
    [Lifecycle(LifecycleAttribute.Kill)]
    Task OnKillAsync();

    /// <summary>
    /// 主入口：把一条消息交给某个会话，跑完（含工具循环）后返回回复。
    /// 会话不存在则新建；同一 <paramref name="sessionRef"/> 的调用串行。
    /// </summary>
    [Method(SideEffect = true, Returns = "reply", TimeoutMs = 120_000)]
    Task<AgentReply> RunAsync(string sessionRef, string text, CancellationToken ct = default);

    /// <summary>中止会话：在跑的那一轮结束后不再继续，返回是否真的中止了东西。</summary>
    [Method(SideEffect = true)]
    Task<bool> CancelAsync(string sessionRef, CancellationToken ct = default);

    /// <summary>模型配置名（空 = 用宿主默认）。</summary>
    [Property]
    string? Model { get; set; }

    /// <summary>单轮允许的工具调用次数上限；用完即收口。</summary>
    [Property]
    int ToolBudget { get; set; }

    /// <summary>会话级额外提示词（空 = 只用模块自带人设）。</summary>
    [Property]
    string? SystemPrompt { get; set; }

    /// <summary>当前在跑的会话数（读上次提交值）。</summary>
    [Property(ReadOnly = true)]
    int ActiveSessions { get; }

    /// <summary>模块状态：idle / busy / cancelled（读上次提交值）。</summary>
    [Property(ReadOnly = true)]
    string State { get; }
}

/// <summary>一次会话调用的产出。属性名投影成 <c>Returns</c> 里的输出口名（当前只进台账）。</summary>
public sealed record AgentReply(
    string SessionRef,
    string Text,
    IReadOnlyList<AgentToolCall> ToolCalls,
    AgentUsage Usage,
    int Rounds)
{
    /// <summary>是否真的跑过（被取消或预算为 0 时也可能有回复）。</summary>
    public bool Completed => Text.Length > 0;
}

/// <summary>一次工具调用（模块内部记录，图只看到汇总）。</summary>
public sealed record AgentToolCall(string Name, string Input, string Result, bool IsError);

/// <summary>token 消耗。语言模型侧字段统一在这里，不再散落。</summary>
public sealed record AgentUsage(int PromptTokens, int CompletionTokens, int TotalTokens)
{
    public static AgentUsage Zero { get; } = new(0, 0, 0);

    public AgentUsage Add(AgentUsage other) => new(
        PromptTokens + other.PromptTokens,
        CompletionTokens + other.CompletionTokens,
        TotalTokens + other.TotalTokens);
}

/// <summary>会话在模块里的状态。<c>State</c> 属性输出其小写名。</summary>
public enum AgentState
{
    Idle,
    Busy,
    Cancelled,
}
