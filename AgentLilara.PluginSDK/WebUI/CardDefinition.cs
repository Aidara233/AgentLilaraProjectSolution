namespace AgentLilara.PluginSDK.WebUI;

public class CardDefinition
{
    public required string Id { get; init; }
    public required CardType Type { get; init; }
    public required string DataSourceId { get; init; }
    public required CardSchema Schema { get; init; }
    public CardLayout Layout { get; init; } = new();
    public string? Title { get; init; }
    /// <summary>行选中时发出的事件名（TableCard/TreeCard）</summary>
    public string? LinkEvent { get; init; }
    /// <summary>监听的事件名，事件触发时以 payload 作为 Extra 刷新数据源</summary>
    public string? ListenEvent { get; init; }
}

public enum CardType
{
    Table, Status, Form, Stream, Chat, Tree, Detail, Action, PropertyGrid, Custom
}

public class CardLayout
{
    public string? MinWidth { get; init; }
    public int PreferredCols { get; init; } = 12;
    public string? Height { get; init; }
    public int Order { get; init; }
    public int RowSpan { get; init; } = 1;
}
