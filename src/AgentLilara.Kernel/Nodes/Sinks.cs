namespace AgentLilara.Kernel.Graph;

/// <summary>数据汇（属性写）：登记一次变量写入，激活成功结束才提交。变量属于图内部，不是外部动作。</summary>
public sealed class WriteVariable : INode
{
    public WriteVariable(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public string Name { get; }

    public NodeKind Kind => NodeKind.Sink;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "value" };

    public IReadOnlyList<string> OutputPorts { get; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        context.PendingWrites.Add((Name, inputs["value"]));
        return Empty;
    }

    private static readonly IReadOnlyDictionary<string, Cell> Empty =
        new Dictionary<string, Cell>(StringComparer.Ordinal);
}

/// <summary>
/// 动作汇块：**不执行动作，只产出意图**（`IntentCatalog` 里的一个动作）。
/// 求解期登记候选；阶段③真正触发时才把 <see cref="ActionIntent"/> 放进上下文。
/// </summary>
public sealed class IntentSink : INode, IIntentSink
{
    private readonly IntentSpec _spec;
    private readonly IReadOnlyList<string> _inputPorts;

    public IntentSink(string id, IntentSpec spec)
    {
        Id = id;
        _spec = spec;

        var ports = new List<string> { "event" };
        ports.AddRange(spec.ArgPorts);
        _inputPorts = ports;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Sink;

    public IReadOnlyList<string> InputPorts => _inputPorts;

    public IReadOnlyList<string> OutputPorts { get; } = Array.Empty<string>();

    /// <summary>节点类型，如 <c>sink.prompt</c>。</summary>
    public string NodeType => _spec.NodeType;

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var enabled = inputs.TryGetValue("event", out var evt) && evt.IsKnown;

        foreach (var port in _spec.ArgPorts)
        {
            if (!inputs.TryGetValue(port, out var arg) || !arg.IsKnown)
            {
                enabled = false;
                break;
            }
        }

        context.SinkCandidates.Add(new SinkCandidate(Id, inputs, enabled));
        return Empty;
    }

    public void Emit(IReadOnlyDictionary<string, Cell> args, EvalContext context)
    {
        var target = _spec.TargetArg is null
            ? context.Event?.ChannelKey ?? string.Empty
            : args.TryGetValue(_spec.TargetArg, out var cell) ? cell.As<string>() ?? string.Empty : string.Empty;

        context.Intents.Add(new ActionIntent(_spec.Kind, target, args));
    }

    private static readonly IReadOnlyDictionary<string, Cell> Empty =
        new Dictionary<string, Cell>(StringComparer.Ordinal);
}
