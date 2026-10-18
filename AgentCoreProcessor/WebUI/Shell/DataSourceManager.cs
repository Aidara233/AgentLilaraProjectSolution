using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Shell;

/// <summary>
/// 页面作用域数据源管理：每个 DynamicPage 页面实例持有一个，负责请求取消、
/// 过期结果丢弃、幂等订阅与释放。有状态数据源应通过 DataSourceDefinition.Factory
/// 在页面初始化时创建，避免跨页面实例共享选中对象。
/// </summary>
internal class DataSourceManager : IDisposable
{
    private sealed class Entry
    {
        public required IDataSource Source { get; init; }
        public required DataSourceDefinition Definition { get; init; }
        public DataSourceState State { get; } = new();
        public readonly object Sync = new();
        public CancellationTokenSource? RequestCts;
        public long Version;
        public IDisposable? Subscription;
        public bool Subscribed;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Dictionary<string, string>? _routeParams;
    private bool _disposed;

    /// <summary>页面级数据源配置错误（如重复 ID），由 DynamicPage 显式展示。</summary>
    public string? ConfigurationError { get; private set; }

    public event Action<string>? OnDataChanged;

    public void Initialize(IReadOnlyList<DataSourceDefinition> definitions, Dictionary<string, string>? routeParams = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _routeParams = routeParams != null
            ? new Dictionary<string, string>(routeParams, StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var def in definitions)
        {
            var entry = new Entry { Source = def.CreateSource(), Definition = def };
            if (!_entries.TryAdd(def.Id, entry))
            {
                ConfigurationError = string.IsNullOrEmpty(ConfigurationError)
                    ? $"页面数据源 ID 重复：{def.Id}。后一个定义已被忽略。"
                    : ConfigurationError + $"\n页面数据源 ID 重复：{def.Id}。后一个定义已被忽略。";
            }
        }
    }

    public async Task<DataResult?> FetchAsync(string? dataSourceId, DataQuery? query = null, CancellationToken ct = default)
    {
        if (_disposed || dataSourceId == null || !_entries.TryGetValue(dataSourceId, out var entry))
            return null;

        CancellationTokenSource requestCts;
        long version;
        var lifetimeToken = _lifetimeCts.Token;
        lock (entry.Sync)
        {
            var previous = entry.RequestCts;
            requestCts = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
            entry.RequestCts = requestCts;
            version = ++entry.Version;
            previous?.Cancel();
            previous?.Dispose();
        }

        var requestToken = requestCts.Token;
        entry.State.IsLoading = true;
        entry.State.Error = null;

        // 注入路由参数，保留原查询其余字段
        if (_routeParams is { Count: > 0 })
        {
            query = query == null
                ? new DataQuery { RouteParams = _routeParams }
                : new DataQuery
                {
                    Page = query.Page, PageSize = query.PageSize, Search = query.Search,
                    SortBy = query.SortBy, SortDesc = query.SortDesc, Filters = query.Filters,
                    Extra = query.Extra, RouteParams = _routeParams
                };
        }

        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var result = await entry.Source.FetchAsync(query, requestToken);
                    if (!TryCompleteCurrent(entry, version))
                        return null;

                    entry.State.LastData = result;
                    entry.State.RetryCount = 0;
                    return result;
                }
                catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    if (attempt < 2)
                    {
                        entry.State.RetryCount = attempt + 1;
                        var delay = (int)Math.Pow(2, attempt) * 1000;
                        try
                        {
                            await Task.Delay(delay, requestToken);
                        }
                        catch (OperationCanceledException)
                        {
                            return null;
                        }
                    }
                    else
                    {
                        if (TryCompleteCurrent(entry, version))
                            entry.State.Error = ex.Message;
                        return null;
                    }
                }
            }

            return null;
        }
        finally
        {
            // 取消只结束请求，不把仍在使用的卡片永久留在加载态。
            if (TryCompleteCurrent(entry, version))
                entry.State.IsLoading = false;

            lock (entry.Sync)
            {
                if (ReferenceEquals(entry.RequestCts, requestCts))
                    entry.RequestCts = null;
            }
            requestCts.Dispose();
        }
    }

    public async Task<ActionResult> SubmitAsync(string? dataSourceId, string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (_disposed || dataSourceId == null || !_entries.TryGetValue(dataSourceId, out var entry))
            return new ActionResult { Success = false, Message = "数据源不存在" };

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
        try
        {
            var result = await entry.Source.SubmitAsync(action, data, linkedCts.Token);
            return _disposed ? new ActionResult { Success = false, Message = "页面已关闭" } : result;
        }
        catch (OperationCanceledException)
        {
            return new ActionResult { Success = false, Message = "操作已取消" };
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Message = ex.Message };
        }
    }

    public void SubscribeAll()
    {
        if (_disposed) return;

        foreach (var entry in _entries.Values)
        {
            if (!entry.Source.SupportsPush) continue;

            lock (entry.Sync)
            {
                if (entry.Subscribed) continue;
                entry.Subscribed = true;
                var subscription = entry.Source.Subscribe(_ =>
                {
                    if (_disposed) return;
                    lock (entry.Sync)
                    {
                        if (!entry.Subscribed) return;
                    }
                    OnDataChanged?.Invoke(entry.Definition.Id);
                });
                entry.Subscription = subscription;
            }
        }
    }

    public DataSourceState? GetState(string? dataSourceId)
        => dataSourceId != null && _entries.TryGetValue(dataSourceId, out var e) ? e.State : null;

    public DataSourceDefinition? GetDefinition(string? dataSourceId)
        => dataSourceId != null && _entries.TryGetValue(dataSourceId, out var e) ? e.Definition : null;

    public DataPagingMode GetPagingMode(string? dataSourceId)
        => GetDefinition(dataSourceId)?.Paging ?? DataPagingMode.Client;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _lifetimeCts.Cancel(); } catch { /* ignore */ }

        foreach (var entry in _entries.Values)
        {
            lock (entry.Sync)
            {
                entry.Subscribed = false;
                entry.Subscription?.Dispose();
                entry.Subscription = null;
                entry.RequestCts?.Cancel();
                entry.RequestCts?.Dispose();
                entry.RequestCts = null;
            }
        }

        _entries.Clear();
        _lifetimeCts.Dispose();
    }

    private bool TryCompleteCurrent(Entry entry, long version)
    {
        if (_disposed) return false;
        lock (entry.Sync)
            return !_disposed && entry.Version == version;
    }
}

public class DataSourceState
{
    public bool IsLoading { get; set; }
    public string? Error { get; set; }
    public DataResult? LastData { get; set; }
    public int RetryCount { get; set; }
}
