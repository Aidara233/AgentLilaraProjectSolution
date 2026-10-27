namespace AgentLilara.GraphRuntime.Graph;

/// <summary>
/// 阶段③的执行结果。方法节点没有输出口，返回值只进台账（供审计与后续续延接线）。
/// </summary>
public sealed record SinkOutcome(string NodeId, bool Success, IReadOnlyDictionary<string, Cell> Outputs, string? Error)
{
    public string? ErrorType { get; init; }

    public static SinkOutcome Ok(string nodeId, IReadOnlyDictionary<string, Cell> outputs) =>
        new(nodeId, true, outputs, null);

    public static SinkOutcome Failed(string nodeId, string error) =>
        new(nodeId, false, EmptyOutputs, error);

    private static readonly IReadOnlyDictionary<string, Cell> EmptyOutputs =
        new Dictionary<string, Cell>(StringComparer.Ordinal);
}

/// <summary>
/// 汇块的自执行能力：求解阶段只登记候选，真正被触发时由实例调用本接口。
/// 没有实现本接口的汇块（如 <c>sink.log</c> 桩）只留 <see cref="SinkInvocation"/> 记录。
/// </summary>
public interface ISinkExecutor
{
    SinkOutcome Execute(IReadOnlyDictionary<string, Cell> args, EvalContext context);
}

/// <summary>
/// 声明"这些输入口可以不接线"。未接线的口不会出现在 <c>Evaluate</c> 的 inputs 里，
/// 节点自己按声明默认值处理（模块可选参数即如此）。
/// </summary>
public interface IOptionalInputs
{
    IReadOnlyCollection<string> OptionalInputPorts { get; }
}
