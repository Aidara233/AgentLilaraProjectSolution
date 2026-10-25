using System.Reflection;
using AgentLilara.Kernel.Graph;

namespace AgentLilara.Kernel.Modules;

/// <summary>
/// 模块方法节点（激活汇）：<c>event</c> 口触发，其余输入口就是实参。
/// 求解阶段只登记候选；实参不齐或类型不对时不调用（登记未触发），调用发生在阶段③。
/// </summary>
public sealed class ModuleMethodNode : INode, ISinkExecutor, IOptionalInputs
{
    private readonly ModuleDescriptor _module;
    private readonly MethodDescriptor _descriptor;
    private readonly MethodInfo _method;
    private readonly object _implementation;
    private readonly IReadOnlyList<ParameterPlan> _plan;
    private readonly IReadOnlyList<string> _inputPorts;

    public ModuleMethodNode(
        string id,
        ModuleDescriptor module,
        MethodDescriptor descriptor,
        MethodInfo method,
        object implementation)
    {
        Id = id;
        _module = module;
        _descriptor = descriptor;
        _method = method;
        _implementation = implementation;

        var plan = new List<ParameterPlan>();
        var ports = new List<string> { "event" };
        var optional = new List<string>();
        var descriptorIndex = 0;

        foreach (var parameter in method.GetParameters())
        {
            // CancellationToken 由框架注入，不占端口
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                plan.Add(new ParameterPlan(parameter, null, Cell.Unknown, Optional: false));
                continue;
            }

            if (descriptorIndex >= descriptor.Params.Count)
            {
                throw new ModuleDescriptorException(
                    $"module '{module.Id}' method '{descriptor.Name}': parameter plan mismatch");
            }

            // 生成期按声明顺序记录端口（已剔除 CancellationToken），这里按序对齐
            var declared = descriptor.Params[descriptorIndex++];
            ports.Add(declared.Name);
            plan.Add(new ParameterPlan(parameter, declared.Name, declared.Default, declared.Optional));
            if (declared.Optional)
            {
                optional.Add(declared.Name);
            }
        }

        if (descriptorIndex != descriptor.Params.Count)
        {
            throw new ModuleDescriptorException(
                $"module '{module.Id}' method '{descriptor.Name}': parameter plan mismatch");
        }

        _plan = plan;
        _inputPorts = ports;
        OptionalInputPorts = optional;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Sink;

    public IReadOnlyList<string> InputPorts => _inputPorts;

    public IReadOnlyList<string> OutputPorts { get; } = Array.Empty<string>();

    public IReadOnlyCollection<string> OptionalInputPorts { get; }

    /// <summary>完整节点类型，如 <c>module.agent.loop.run</c>（审计用）。</summary>
    public string NodeType => ModuleNodeTypes.Method(_module.Id, _descriptor.Name);

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var ready = inputs.TryGetValue("event", out var evt) && evt.IsKnown && ArgumentsReady(inputs);
        context.SinkCandidates.Add(new SinkCandidate(Id, inputs, ready));
        return Empty;
    }

    public SinkOutcome Execute(IReadOnlyDictionary<string, Cell> args, EvalContext context)
    {
        var values = new object?[_plan.Count];
        for (var i = 0; i < _plan.Count; i++)
        {
            var step = _plan[i];
            if (step.PortName is null)
            {
                values[i] = CancellationToken.None;
                continue;
            }

            var cell = args.TryGetValue(step.PortName, out var provided) ? provided : step.Default;
            if (!ModuleValueType.TryToClr(cell, step.Parameter.ParameterType, out var value, out var error))
            {
                return SinkOutcome.Failed(Id, $"arg '{step.PortName}': {error}");
            }

            values[i] = value;
        }

        object? returned;
        try
        {
            returned = Invoke(_method, _implementation, values);
        }
        catch (Exception ex)
        {
            return SinkOutcome.Failed(Id, $"module '{_module.Id}' method '{_descriptor.Name}' threw: {ex.Message}");
        }

        return SinkOutcome.Ok(Id, MapOutputs(returned));
    }

    /// <summary>实参是否齐备：必需参数必须在场且已知，可选参数若接了线也必须已知。</summary>
    private bool ArgumentsReady(IReadOnlyDictionary<string, Cell> inputs)
    {
        foreach (var step in _plan)
        {
            if (step.PortName is null || step.Optional)
            {
                continue;
            }

            if (!inputs.TryGetValue(step.PortName, out var cell) || !cell.IsKnown)
            {
                return false;
            }
        }

        foreach (var step in _plan)
        {
            if (step.PortName is null || !step.Optional || !inputs.TryGetValue(step.PortName, out var cell))
            {
                continue;
            }

            if (cell.IsNoEvent)
            {
                return false; // 接了线但本拍没有值：不调用，避免把"没有"当成实参
            }
        }

        return true;
    }

    private IReadOnlyDictionary<string, Cell> MapOutputs(object? returned)
    {
        if (_descriptor.Returns.Count == 0)
        {
            return Empty;
        }

        var outputs = new Dictionary<string, Cell>(StringComparer.Ordinal);
        if (_descriptor.Returns.Count == 1)
        {
            outputs[_descriptor.Returns[0]] = ModuleValueType.FromClr(returned);
            return outputs;
        }

        foreach (var name in _descriptor.Returns)
        {
            var property = returned?.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(p => ModuleNames.Camel(p.Name) == name);

            outputs[name] = property is null || returned is null
                ? Cell.NoEvent
                : ModuleValueType.FromClr(property.GetValue(returned));
        }

        return outputs;
    }

    /// <summary>同步桥：接口声明可以返回 Task，但内核目前没有异步执行管线，只能就地等。</summary>
    private static object? Invoke(MethodInfo method, object implementation, object?[] values)
    {
        object? returned;
        try
        {
            returned = method.Invoke(implementation, values);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }

        switch (returned)
        {
            case Task task:
                task.GetAwaiter().GetResult();
                return task.GetType().GetProperty("Result")?.GetValue(task);

            case ValueTask valueTask:
                valueTask.AsTask().GetAwaiter().GetResult();
                return null;

            default:
                return returned;
        }
    }

    private sealed record ParameterPlan(ParameterInfo Parameter, string? PortName, Cell Default, bool Optional);

    private static readonly IReadOnlyDictionary<string, Cell> Empty =
        new Dictionary<string, Cell>(StringComparer.Ordinal);
}

/// <summary>模块属性读节点（数据源）：读上次提交值。</summary>
public sealed class ModulePropertyReadNode : INode
{
    private readonly ModuleDescriptor _module;
    private readonly PropertyDescriptor _descriptor;
    private readonly PropertyInfo _property;
    private readonly object _implementation;

    public ModulePropertyReadNode(
        string id,
        ModuleDescriptor module,
        PropertyDescriptor descriptor,
        PropertyInfo property,
        object implementation)
    {
        Id = id;
        _module = module;
        _descriptor = descriptor;
        _property = property;
        _implementation = implementation;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Source;

    public IReadOnlyList<string> InputPorts { get; } = Array.Empty<string>();

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "value" };

    public string NodeType => ModuleNodeTypes.PropertyRead(_module.Id, _descriptor.Name);

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context) =>
        new Dictionary<string, Cell>(StringComparer.Ordinal)
        {
            ["value"] = ModuleValueType.FromClr(_property.GetValue(_implementation)),
        };
}

/// <summary>模块属性写节点（数据汇）：<c>event</c> 触发、<c>value</c> 是写入值；阶段③才真正写。</summary>
public sealed class ModulePropertyWriteNode : INode, ISinkExecutor
{
    private readonly ModuleDescriptor _module;
    private readonly PropertyDescriptor _descriptor;
    private readonly PropertyInfo _property;
    private readonly object _implementation;

    public ModulePropertyWriteNode(
        string id,
        ModuleDescriptor module,
        PropertyDescriptor descriptor,
        PropertyInfo property,
        object implementation)
    {
        Id = id;
        _module = module;
        _descriptor = descriptor;
        _property = property;
        _implementation = implementation;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Sink;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "event", "value" };

    public IReadOnlyList<string> OutputPorts { get; } = Array.Empty<string>();

    public string NodeType => ModuleNodeTypes.PropertyWrite(_module.Id, _descriptor.Name);

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var enabled = inputs.TryGetValue("event", out var evt)
                      && evt.IsKnown
                      && inputs.TryGetValue("value", out var value)
                      && value.IsKnown;

        context.SinkCandidates.Add(new SinkCandidate(Id, inputs, enabled));
        return Empty;
    }

    public SinkOutcome Execute(IReadOnlyDictionary<string, Cell> args, EvalContext context)
    {
        if (_property.SetMethod is null)
        {
            return SinkOutcome.Failed(Id, $"module '{_module.Id}' property '{_descriptor.Name}' is read-only");
        }

        var cell = args.TryGetValue("value", out var provided) ? provided : Cell.Unknown;
        if (!ModuleValueType.TryToClr(cell, _property.PropertyType, out var value, out var error))
        {
            return SinkOutcome.Failed(Id, $"value: {error}");
        }

        try
        {
            _property.SetValue(_implementation, value);
        }
        catch (Exception ex)
        {
            return SinkOutcome.Failed(Id, $"module '{_module.Id}' property '{_descriptor.Name}' threw: {ex.Message}");
        }

        return SinkOutcome.Ok(Id, Empty);
    }

    private static readonly IReadOnlyDictionary<string, Cell> Empty =
        new Dictionary<string, Cell>(StringComparer.Ordinal);
}
