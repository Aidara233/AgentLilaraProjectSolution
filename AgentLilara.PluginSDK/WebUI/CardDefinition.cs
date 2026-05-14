namespace AgentLilara.PluginSDK.WebUI;

public class CardDefinition
{
    public required string Id { get; init; }
    public required CardType Type { get; init; }
    public required string DataSourceId { get; init; }
    public required CardSchema Schema { get; init; }
    public CardLayout Layout { get; init; } = new();
    public string? Title { get; init; }
}

public enum CardType
{
    Table, Status, Form, Stream, Chat, Tree, Detail, Custom
}

public class CardLayout
{
    public string? MinWidth { get; init; }
    public int PreferredCols { get; init; } = 12;
    public string? Height { get; init; }
    public int Order { get; init; }
}
