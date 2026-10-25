using AgentLilara.GraphRuntime.Contracts;

namespace AgentLilara.GraphRuntime.Graph;

/// <summary>逻辑：读事件的一个字段（`event.get`）。字段名在加载期校验，拼错直接失败。</summary>
public sealed class EventField : INode
{
    /// <summary>允许读取的字段名。加字段 = 改这里 + 改 `05-nodes.md`。</summary>
    public static readonly IReadOnlyList<string> AllowedFields = new[]
    {
        "text", "sender", "channel", "adapter", "kind", "mentions", "isReply", "received", "dedupe",
    };

    public EventField(string id, string field)
    {
        Id = id;
        Field = field;
    }

    public string Id { get; }

    public string Field { get; }

    public NodeKind Kind => NodeKind.Logic;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "event" };

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "value" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var upstream = inputs["event"];
        if (upstream.IsNoEvent)
        {
            return new Dictionary<string, Cell>(StringComparer.Ordinal) { ["value"] = Cell.NoEvent };
        }

        if (upstream.Payload is not InboundEvent @event)
        {
            return new Dictionary<string, Cell>(StringComparer.Ordinal) { ["value"] = Cell.Unknown };
        }

        var value = Field switch
        {
            "text" => Cell.Of(@event.Text),
            "sender" => @event.SenderId is null ? Cell.Of(string.Empty) : Cell.Of(@event.SenderId),
            "channel" => Cell.Of(@event.ChannelKey),
            "adapter" => Cell.Of(@event.AdapterId),
            "kind" => Cell.Of(@event.Kind.ToString()),
            "mentions" => Cell.Of(@event.MentionsBot),
            "isReply" => Cell.Of(@event.IsReplyToBot),
            "received" => Cell.Of(@event.ReceivedAt.ToString("O")),
            "dedupe" => Cell.Of(@event.DedupeKey),
            _ => Cell.Unknown,
        };

        return new Dictionary<string, Cell>(StringComparer.Ordinal) { ["value"] = value };
    }
}

/// <summary>逻辑：本事件是否点名了 bot。</summary>
public sealed class EventMention : INode
{
    public EventMention(string id = "event.mention")
    {
        Id = id;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Logic;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "event" };

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "result" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var upstream = inputs["event"];
        // 上游无事件 → 下游也是无事件（已确定），不是未知
        var result = upstream.IsNoEvent
            ? Cell.NoEvent
            : Cell.Of(upstream.Payload is InboundEvent { MentionsBot: true });

        return new Dictionary<string, Cell>(StringComparer.Ordinal) { ["result"] = result };
    }
}

/// <summary>逻辑：逻辑与（无事件按 false 处理）。</summary>
public sealed class LogicAnd : INode
{
    public LogicAnd(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Logic;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "a", "b" };

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "result" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var value = inputs["a"].AsBool() && inputs["b"].AsBool();
        return new Dictionary<string, Cell>(StringComparer.Ordinal) { ["result"] = Cell.Of(value) };
    }
}

/// <summary>逻辑（闸门）：条件通过才让事件继续，否则输出无事件。</summary>
public sealed class GateIf : INode
{
    public GateIf(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Logic;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "event", "cond" };

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "event" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var evt = inputs["event"];
        var pass = evt.IsKnown && inputs["cond"].AsBool();
        return new Dictionary<string, Cell>(StringComparer.Ordinal)
        {
            ["event"] = pass ? evt : Cell.NoEvent,
        };
    }
}

/// <summary>逻辑：数值比较 value &gt;= threshold。</summary>
public sealed class CompareGe : INode
{
    public CompareGe(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Logic;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "value", "threshold" };

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "result" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var value = inputs["value"].AsInt();
        var threshold = inputs["threshold"].AsInt();
        return new Dictionary<string, Cell>(StringComparer.Ordinal) { ["result"] = Cell.Of(value >= threshold) };
    }
}

/// <summary>逻辑：整数加法。</summary>
public sealed class AddInt : INode
{
    public AddInt(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Logic;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "a", "b" };

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "result" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context) =>
        new Dictionary<string, Cell>(StringComparer.Ordinal)
        {
            ["result"] = Cell.Of(inputs["a"].AsInt() + inputs["b"].AsInt()),
        };
}

/// <summary>逻辑：取列表的第一个值。</summary>
public sealed class ListFirst : INode
{
    public ListFirst(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Logic;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "list" };

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "value", "found" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var list = inputs["list"];

        if (list.IsNoEvent)
        {
            return new Dictionary<string, Cell>(StringComparer.Ordinal)
            {
                ["value"] = Cell.NoEvent,
                ["found"] = Cell.NoEvent,
            };
        }

        if (list.Payload is IReadOnlyList<string> { Count: > 0 } items)
        {
            return new Dictionary<string, Cell>(StringComparer.Ordinal)
            {
                ["value"] = Cell.Of(items[0]),
                ["found"] = Cell.Of(true),
            };
        }

        return new Dictionary<string, Cell>(StringComparer.Ordinal)
        {
            ["value"] = Cell.NoEvent,
            ["found"] = Cell.Of(false),
        };
    }
}

/// <summary>逻辑：逻辑或（无事件按 false 处理）。</summary>
public sealed class LogicOr : INode
{
    public LogicOr(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Logic;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "a", "b" };

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "result" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context) =>
        new Dictionary<string, Cell>(StringComparer.Ordinal)
        {
            ["result"] = Cell.Of(inputs["a"].AsBool() || inputs["b"].AsBool()),
        };
}

/// <summary>文本是否包含指定子串（序数匹配，避免文化相关行为）。</summary>
public sealed class TextContains : INode
{
    public TextContains(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Logic;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "text", "needle" };

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "result" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var text = inputs["text"];
        var needle = inputs["needle"];
        var result = text.State == CellState.Unknown || needle.State == CellState.Unknown
            ? Cell.Unknown
            : text.IsNoEvent || needle.IsNoEvent
                ? Cell.NoEvent
                : Cell.Of((text.As<string>() ?? string.Empty).Contains(
                    needle.As<string>() ?? string.Empty,
                    StringComparison.Ordinal));
        return new Dictionary<string, Cell>(StringComparer.Ordinal) { ["result"] = result };
    }
}

/// <summary>
/// 逻辑：条件取值 <c>cond ? a : b</c>（两侧都已求值，只做选择，不做惰性分支）。
/// 用于"条件赋值"：把"什么情况下重置/沿用"写成一个值，避免对同一变量写两次。
/// </summary>
public sealed class SelectValue : INode
{
    public SelectValue(string id)
    {
        Id = id;
    }

    public string Id { get; }

    public NodeKind Kind => NodeKind.Logic;

    public IReadOnlyList<string> InputPorts { get; } = new[] { "cond", "a", "b" };

    public IReadOnlyList<string> OutputPorts { get; } = new[] { "result" };

    public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
    {
        var cond = inputs["cond"];
        var a = inputs["a"];
        var b = inputs["b"];

        // 条件未知 → 结果未知；条件无事件 → 取 b
        Cell result = cond.State switch
        {
            CellState.Unknown => Cell.Unknown,
            CellState.NoEvent => b,
            _ => cond.AsBool() ? a : b,
        };

        return new Dictionary<string, Cell>(StringComparer.Ordinal) { ["result"] = result };
    }
}
