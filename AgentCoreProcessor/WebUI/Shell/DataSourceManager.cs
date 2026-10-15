using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Shell;

internal class DataSourceManager : IDisposable
{
    private readonly ConcurrentDictionary<string, IDataSource> _sources = new();
    private readonly ConcurrentDictionary<string, IDisposable?> _subscriptions = new();
    private readonly ConcurrentDictionary<string, DataSourceState> _states = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requestCts = new();
    private readonly ConcurrentDictionary<string, long> _requestVersions = new();
    private CancellationTokenSource _lifetimeCts = new();
    private Dictionary<string, string>? _routeParams;
    private long _generation;
    private bool _disposed;

    public event Action<string>? OnDataChanged;

    public void Initialize(IReadOnlyList<DataSourceDefinition> definitions, Dictionary<string, string>? routeParams = null)
    {
        if (_disposed) return;

        CancelRequestsAndSubscriptions();
        _generation++;
        _routeParams = routeParams != null
            ? new Dictionary<string, string>(routeParams, StringComparer.OrdinalIgnoreCase)
            : null;
        _sources.Clear();
        _states.Clear();
        _requestVersions.Clear();
        foreach (var def in definitions)
        {
            _sources[def.Id] = def.Source;
            _states[def.Id] = new DataSourceState();
        }
    }

    public async Task<DataResult?> FetchAsync(string? dataSourceId, DataQuery? query = null, CancellationToken ct = default)
    {
        if (_disposed || dataSourceId == null || !_sources.TryGetValue(dataSourceId, out var source))
            return null;

        if (!_states.TryGetValue(dataSourceId, out var state))
            return null;

        var version = _requestVersions.AddOrUpdate(dataSourceId, 1, static (_, current) => current + 1);
        var requestCts = new CancellationTokenSource();
        var previousCts = _requestCts.AddOrUpdate(dataSourceId, requestCts, (_, previous) =>
        {
            previous.Cancel();
            previous.Dispose();
            return requestCts;
        });
        if (!ReferenceEquals(previousCts, requestCts))
            previousCts.Dispose();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token, requestCts.Token);
        var requestToken = linkedCts.Token;
        state.IsLoading = true;
        state.Error = null;

        // 注入路由参数
        if (_routeParams != null && _routeParams.Count > 0)
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

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var result = await source.FetchAsync(query, requestToken);
                if (_disposed || requestToken.IsCancellationRequested ||
                    !_requestVersions.TryGetValue(dataSourceId, out var currentVersion) || currentVersion != version)
                    return null;

                state.IsLoading = false;
                state.LastData = result;
                state.RetryCount = 0;
                return result;
            }
            catch (OperationCanceledException) when (requestToken.IsCancellationRequested)
            {
                if (!_disposed && _requestVersions.TryGetValue(dataSourceId, out var currentVersion) && currentVersion == version)
                    state.IsLoading = false;
                return null;
            }
            catch (Exception ex)
            {
                if (attempt < 2)
                {
                    state.RetryCount = attempt + 1;
                    var delay = (int)Math.Pow(2, attempt) * 1000;
                    await Task.Delay(delay, requestToken);
                }
                else
                {
                    if (!_disposed && _requestVersions.TryGetValue(dataSourceId, out var currentVersion) && currentVersion == version)
                    {
                        state.IsLoading = false;
                        state.Error = ex.Message;
                    }
                    return null;
                }
            }
        }

        return null;
    }

    public async Task<ActionResult> SubmitAsync(string? dataSourceId, string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (dataSourceId == null || !_sources.TryGetValue(dataSourceId, out var source))
            return new ActionResult { Success = false, Message = "数据源不存在" };

        try
        {
            return await source.SubmitAsync(action, data, ct);
        }
        catch (Exception ex)
        {
            return new ActionResult { Success = false, Message = ex.Message };
        }
    }

    public void SubscribeAll()
    {
        if (_disposed) return;

        foreach (var (id, source) in _sources)
        {
            if (!source.SupportsPush) continue;
            if (_subscriptions.TryRemove(id, out var oldSubscription))
                oldSubscription?.Dispose();

            var generation = _generation;
            var subscription = source.Subscribe(payload =>
            {
                if (_disposed || generation != _generation) return;
                OnDataChanged?.Invoke(id);
            });
            _subscriptions[id] = subscription;
        }
    }

    public DataSourceState? GetState(string? dataSourceId)
        => dataSourceId != null && _states.TryGetValue(dataSourceId, out var s) ? s : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelRequestsAndSubscriptions();
        _lifetimeCts.Dispose();
        _sources.Clear();
        _states.Clear();
    }

    private void CancelRequestsAndSubscriptions()
    {
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();
        _lifetimeCts = new CancellationTokenSource();

        foreach (var cts in _requestCts.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
        _requestCts.Clear();

        foreach (var sub in _subscriptions.Values)
            sub?.Dispose();
        _subscriptions.Clear();
    }
}

public class DataSourceState
{
    public bool IsLoading { get; set; }
    public string? Error { get; set; }
    public DataResult? LastData { get; set; }
    public int RetryCount { get; set; }
}
