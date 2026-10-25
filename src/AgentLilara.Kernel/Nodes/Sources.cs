using AgentLilara.Kernel.Contracts;

namespace AgentLilara.Kernel.Graph;

/// <summary>激活源：收到消息事件（每个图**恰好一个**激活源）。输出事件本身，下游可读任意字段。</summary>
public sealed class OnMessageEvent : INode
{
    public OnMessageEvent(string id = "on.message")
    {
        Id = id;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Source;

    public bool IsActivationSource => true;

    public IReadOnlyList<string> InputPorts { get; } = Array.Empty<string>();

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "event", "channel" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var @event = context.Event;
        return new Dictionary<string, Cell>(StringComparer.Ordinal)
        {
            ["event"] = @event is null ? Cell.NoEvent : Cell.Of(@event),
            ["channel"] = @event is null ? Cell.NoEvent : Cell.Of(@event.ChannelKey),
        };
    }
}

/// <summary>激活源：时钟沿（动态图专用；归档保留，`preset.digest` 会用到）。</summary>
public sealed class OnClock : INode
{
    public OnClock(string id = "on.clock")
    {
        Id = id;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Source;

    public bool IsActivationSource => true;

    public IReadOnlyList<string> InputPorts { get; } = Array.Empty<string>();

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "event" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context) =>
        new Dictionary<string, Cell>(StringComparer.Ordinal)
        {
            ["event"] = context.Event is null ? Cell.NoEvent : Cell.Of(context.Event),
        };
}

/// <summary>数据源：常量（强制绑定、测试、给方法喂固定实参）。</summary>
public sealed class ConstValue : INode
{
    public ConstValue(string id, Cell value)
    {
        Id = id;
        Value = value;
    }

    public string Id { get; }

    public Cell Value { get; }

    public NodeKind Kind => NodeKind.Source;

    public IReadOnlyList<string> InputPorts { get; } = Array.Empty<string>();

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "value" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context) =>
        new Dictionary<string, Cell>(StringComparer.Ordinal) { ["value"] = Value };
}

/// <summary>数据源：实例变量（本拍快照）。</summary>
public sealed class ReadVariable : INode
{
    public ReadVariable(string id, string name)
    {
        Id = id;
        Name = name;
    }

    public string Id { get; }

    public string Name { get; }

    public NodeKind Kind => NodeKind.Source;

    public IReadOnlyList<string> InputPorts { get; } = Array.Empty<string>();

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "value" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var value = context.Variables.TryGetValue(Name, out var cell) ? cell : Cell.Unknown;
        return new Dictionary<string, Cell>(StringComparer.Ordinal) { ["value"] = value };
    }
}
