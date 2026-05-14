using System.Collections.Generic;

namespace AgentLilara.PluginSDK.WebUI;

public class PageDefinition
{
    public required string Route { get; init; }
    public required PageMeta Meta { get; init; }
    public required IReadOnlyList<CardDefinition> Cards { get; init; }
    public required IReadOnlyList<DataSourceDefinition> DataSources { get; init; }
}

public class PageMeta
{
    public required string Title { get; init; }
    public string? Icon { get; init; }
    public string? Group { get; init; }
    public int Order { get; init; }
    public bool DefaultCollapsed { get; init; }
}

public class DataSourceDefinition
{
    public required string Id { get; init; }
    public required IDataSource Source { get; init; }
}

public interface IDataSource { }
