namespace AgentLilara.Kernel.Graph;

/// <summary>
/// 节点输出口的三态：Unknown = 悬空未求解；NoEvent = 本拍没有事件流过（已确定）；
/// Known = 已求出确定值。
/// </summary>
public enum CellState
{
    Unknown,
    NoEvent,
    Known,
}

/// <summary>一次求解中某节点某输出口的值。每个输出口在一次求解中只写一次。</summary>
public readonly struct Cell
{
    public CellState State { get; }

    public object? Payload { get; }

    private Cell(CellState state, object? payload)
    {
        State = state;
        Payload = payload;
    }

    public static readonly Cell Unknown = new(CellState.Unknown, null);

    public static readonly Cell NoEvent = new(CellState.NoEvent, null);

    public static Cell Of(object? payload) => new(CellState.Known, payload);

    public static Cell Of(bool value) => new(CellState.Known, value);

    public static Cell Of(int value) => new(CellState.Known, value);

    public static Cell Of(string value) => new(CellState.Known, value);

    public bool IsDetermined => State != CellState.Unknown;

    public bool IsKnown => State == CellState.Known;

    public bool IsNoEvent => State == CellState.NoEvent;

    public T? As<T>() => State == CellState.Known && Payload is T typed ? typed : default;

    public bool AsBool() => As<bool>();

    public int AsInt() => As<int>();

    public override string ToString() => State switch
    {
        CellState.Unknown => "unknown",
        CellState.NoEvent => "no-event",
        _ => $"known({Payload})",
    };
}
