using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.WebUI;

/// <summary>
/// 页面数据源声明。页面作用域会变化的数据源应使用 <see cref="Factory"/>，
/// 由每个页面实例创建独立数据源，避免跨标签页/跨页面共享选中状态。
/// </summary>
public class DataSourceDefinition
{
    public required string Id { get; init; }

    /// <summary>共享数据源实例。仅用于无状态数据源；有会话状态时请改用 <see cref="Factory"/>。</summary>
    public IDataSource? Source { get; init; }

    /// <summary>页面作用域数据源工厂。每个页面初始化调用一次。</summary>
    public Func<IDataSource>? Factory { get; init; }

    /// <summary>分页语义：Client = 数据源返回完整集合；Server = 数据源按 DataQuery.Page/PageSize 返回当前页。</summary>
    public DataPagingMode Paging { get; init; } = DataPagingMode.Client;

    public IDataSource CreateSource()
        => Source
           ?? Factory?.Invoke()
           ?? throw new InvalidOperationException($"Data source '{Id}' must declare Source or Factory.");
}

/// <summary>表格分页语义。显式声明，避免用 TotalCount 是否存在猜测。</summary>
public enum DataPagingMode
{
    /// <summary>数据源返回完整集合，表格在本地搜索/排序/分页。</summary>
    Client,
    /// <summary>数据源负责搜索/排序/分页，表格只展示当前页并使用 TotalCount 计算总页数。</summary>
    Server
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
    public Dictionary<string, string>? RouteParams { get; init; }
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
    /// <summary>服务端分页时的过滤后总数；客户端分页时仅作提示。</summary>
    public int? TotalCount { get; init; }
    public JsonNode? Meta { get; init; }
}

public class ActionResult
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public JsonNode? Data { get; init; }
}
