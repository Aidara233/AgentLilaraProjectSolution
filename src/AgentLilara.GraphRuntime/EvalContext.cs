using AgentLilara.GraphRuntime.Contracts;

namespace AgentLilara.GraphRuntime.Graph;

/// <summary>一次激活（一条事件驱动的一次求解）的全部上下文。</summary>
public sealed class EvalContext
{
    public EvalContext(string traceId, long seq, InboundEvent? @event = null, DateTime? time = null)
    {
        TraceId = traceId;
        Seq = seq;
        Event = @event;
        Time = time ?? (@event?.ReceivedAt.UtcDateTime ?? DateTime.UtcNow);
    }

    /// <summary>因果链标识：续延放行/派生调用时保持不变或指向父 trace。</summary>
    public string TraceId { get; }

    public string? ParentTraceId { get; init; }

    public long Seq { get; }

    /// <summary>本拍驱动这次激活的入站事件（图的唯一输入）。</summary>
    public InboundEvent? Event { get; }

    public DateTime Time { get; }

    /// <summary>变量快照：本拍内只读，不随写入变化。</summary>
    public IReadOnlyDictionary<string, Cell> Variables { get; internal set; } = new Dictionary<string, Cell>();

    /// <summary>本拍待提交的变量写入（激活成功结束后才生效）。</summary>
    public List<(string Name, Cell Value)> PendingWrites { get; } = new();

    /// <summary>汇块候选：是否真正触发由实例按图类别决定（静态电平 / 动态上升沿）。</summary>
    public List<SinkCandidate> SinkCandidates { get; } = new();

    /// <summary>阶段③产出的动作意图：图不执行动作，只表达"想做什么"。</summary>
    public List<ActionIntent> Intents { get; } = new();

    /// <summary>实际求值顺序，用于断言确定性与审计。</summary>
    public List<string> EvaluationOrder { get; } = new();
}

/// <summary>一次汇块调用：节点 + 已求值的实参。</summary>
public sealed record SinkInvocation(string NodeId, IReadOnlyDictionary<string, Cell> Args);

/// <summary>
/// 汇块候选：节点在求解阶段登记"我想被调用 / 不想"，是否真的触发由实例决定
/// （静态图看电平，动态图看上升沿）。
/// </summary>
public sealed record SinkCandidate(string NodeId, IReadOnlyDictionary<string, Cell> Args, bool Enabled);
