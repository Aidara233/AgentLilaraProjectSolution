using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.WebUI;

public class DataSourceDefinition
{
    public required string Id { get; init; }
    public required IDataSource Source { get; init; }
}

public interface IDataSource
{
    Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default);
    Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default);
    bool SupportsPush { get; }
    IDisposable? Subscribe(Action<JsonNode?> callback);
}

public class DataQuery
{
    public int? Page { get; init; }
    public int? PageSize { get; init; }
    public string? Search { get; init; }
    public string? SortBy { get; init; }
    public bool SortDesc { get; init; }
    public List<DataFilter>? Filters { get; init; }
    public JsonNode? Extra { get; init; }
}

public class DataFilter
{
    public required string Field { get; init; }
    public required string Operator { get; init; }
    public required string Value { get; init; }
}

public class DataResult
{
    public required JsonNode Data { get; init; }
    public int? TotalCount { get; init; }
    public JsonNode? Meta { get; init; }
}

public class ActionResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public JsonNode? Data { get; init; }
}
