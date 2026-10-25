namespace AgentLilara.GraphRuntime.Graph;

/// <summary>
/// 图实例：持有变量与上次提交快照。一次激活 = 求解 → 成功后才提交变量写入、产出意图、执行即时汇块。
/// 求解无效时本拍不留下任何副作用（不提交变量、不产出意图）。
/// </summary>
public sealed class GraphInstance
{
    private readonly Dictionary<string, Cell> _variables = new(StringComparer.Ordinal);

    public GraphInstance(GraphClass @class, string? instanceKey = null)
    {
        Class = @class;
        InstanceKey = instanceKey ?? $"{@class.ClassId}@default";
        foreach (var decl in @class.Variables)
        {
            _variables[decl.Name] = decl.InitialValue;
        }
    }

    public GraphClass Class { get; }

    public string InstanceKey { get; }

    /// <summary>当前已提交的变量（对外只读；全部公开）。</summary>
    public IReadOnlyDictionary<string, Cell> Variables => _variables;

    /// <summary>上次激活产出的动作意图（图不执行动作，骨架负责执行）。</summary>
    public IReadOnlyList<ActionIntent> LastIntents { get; private set; } = Array.Empty<ActionIntent>();

    /// <summary>上次成功激活中真正触发的汇块（含即时执行与意图产出）。</summary>
    public IReadOnlyList<SinkInvocation> LastSinkInvocations { get; private set; } = Array.Empty<SinkInvocation>();

    /// <summary>每个汇块上一拍的使能值（上升沿判定用；动态图专用，归档保留）。</summary>
    public IReadOnlyDictionary<string, bool> SinkLatches => _sinkLatches;

    private readonly Dictionary<string, bool> _sinkLatches = new(StringComparer.Ordinal);

    /// <summary>上次激活中"登记了但没触发"的汇块（用于解释行为）。</summary>
    public IReadOnlyList<string> LastSuppressedSinks { get; private set; } = Array.Empty<string>();

    /// <summary>上次激活中执行过的即时汇块结果（实现了 <see cref="ISinkExecutor"/> 的节点）。</summary>
    public IReadOnlyList<SinkOutcome> LastSinkOutcomes { get; private set; } = Array.Empty<SinkOutcome>();

    /// <summary>上次激活的求解结果。</summary>
    public SolveResult? LastResult { get; private set; }

    /// <summary>执行一次激活。成功才提交变量、产出意图；失败什么都不提交。</summary>
    public SolveResult Run(EvalContext context)
    {
        // 快照：本拍内变量只读，写入先进 PendingWrites
        context.Variables = new Dictionary<string, Cell>(_variables, StringComparer.Ordinal);

        var result = Solver.Solve(Class, context);
        LastResult = result;

        if (!result.Success)
        {
            // 求解无效：不提交变量、不触发汇、不产出意图、也不更新 latch
            LastIntents = Array.Empty<ActionIntent>();
            LastSinkInvocations = Array.Empty<SinkInvocation>();
            LastSuppressedSinks = Array.Empty<string>();
            LastSinkOutcomes = Array.Empty<SinkOutcome>();
            return result;
        }

        foreach (var (name, value) in context.PendingWrites)
        {
            _variables[name] = value;
        }

        var fired = new List<SinkInvocation>();
        var suppressed = new List<string>();
        foreach (var candidate in context.SinkCandidates)
        {
            _sinkLatches.TryGetValue(candidate.NodeId, out var previous);
            var fire = Class.Kind == GraphKind.Dynamic
                ? candidate.Enabled && !previous   // 动态图：上升沿
                : candidate.Enabled;               // 静态图：电平（一次激活只可能触发一次）

            _sinkLatches[candidate.NodeId] = candidate.Enabled;

            if (fire)
            {
                fired.Add(new SinkInvocation(candidate.NodeId, candidate.Args));
            }
            else if (candidate.Enabled)
            {
                suppressed.Add(candidate.NodeId);
            }
        }

        LastSinkInvocations = fired;
        LastSuppressedSinks = suppressed;

        // 阶段③：变量已提交。派发型动作产出意图，即时型动作就地执行。
        var outcomes = new List<SinkOutcome>();
        foreach (var invocation in fired)
        {
            var node = Class.FindNode(invocation.NodeId);

            if (node is IIntentSink intentSink)
            {
                intentSink.Emit(invocation.Args, context);
                continue;
            }

            if (node is not ISinkExecutor executor)
            {
                continue;
            }

            try
            {
                outcomes.Add(executor.Execute(invocation.Args, context));
            }
            catch (Exception ex)
            {
                outcomes.Add(SinkOutcome.Failed(invocation.NodeId, ex.Message));
            }
        }

        LastIntents = context.Intents.ToList();
        LastSinkOutcomes = outcomes;
        return result;
    }

    public Cell ReadVariable(string name) =>
        _variables.TryGetValue(name, out var cell) ? cell : Cell.Unknown;
}
