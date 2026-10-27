using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;

namespace AgentLilara.GraphSandbox;

/// <summary>一拍的结果：静态图响应一条事件 + 动态图推进一个时钟沿。</summary>
public sealed record SandboxReport(
    long Seq,
    InboundEvent? Event,
    bool StaticOk,
    IReadOnlyList<string> StaticUnresolved,
    IReadOnlyList<string> StaticOrder,
    IReadOnlyList<ActionIntent> StaticIntents,
    bool DynamicOk,
    IReadOnlyList<string> DynamicUnresolved,
    IReadOnlyList<ActionIntent> DynamicIntents,
    IReadOnlyList<string> DynamicSuppressed,
    IReadOnlyDictionary<string, Cell> DynamicSnapshot,
    IReadOnlyDictionary<string, Cell> DynamicVariables);

/// <summary>
/// 沙箱运行器：把**事件**喂给静态图（决策图），再推进动态图一个时钟沿。
/// 图只产出意图，这里不执行任何真实动作——骨架还没接。
/// </summary>
public sealed class SandboxRunner
{
    private readonly GraphInstance _static;
    private readonly GraphInstance _dynamic;

    public SandboxRunner(GraphClass staticGraph, GraphClass dynamicGraph)
    {
        _static = new GraphInstance(staticGraph);
        _dynamic = new GraphInstance(dynamicGraph);
    }

    public IReadOnlyDictionary<string, Cell> StaticVariables => _static.Variables;

    public IReadOnlyDictionary<string, Cell> DynamicVariables => _dynamic.Variables;

    public SandboxReport Tick(InboundEvent? @event, long seq)
    {
        var staticContext = new EvalContext($"trace-{seq}", seq, @event);
        var staticResult = _static.Run(staticContext);

        var clock = InboundEvent.Clock(seq);
        var dynamicContext = new EvalContext($"trace-{seq}-clock", seq, clock);
        var dynamicResult = _dynamic.Run(dynamicContext);

        return new SandboxReport(
            seq,
            @event,
            staticResult.Success,
            staticResult.Unresolved,
            staticContext.EvaluationOrder,
            _static.LastIntents,
            dynamicResult.Success,
            dynamicResult.Unresolved,
            _dynamic.LastIntents,
            _dynamic.LastSuppressedSinks,
            new Dictionary<string, Cell>(dynamicContext.Variables, StringComparer.Ordinal),
            new Dictionary<string, Cell>(_dynamic.Variables, StringComparer.Ordinal));
    }
}
