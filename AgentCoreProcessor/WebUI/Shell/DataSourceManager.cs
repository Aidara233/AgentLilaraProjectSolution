using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Shell;

internal class DataSourceManager : IDisposable
{
    private readonly Dictionary<string, IDataSource> _sources = new();
    private readonly Dictionary<string, IDisposable?> _subscriptions = new();
    private readonly Dictionary<string, DataSourceState> _states = new();

    public event Action<string>? OnDataChanged;

    public void Initialize(IReadOnlyList<DataSourceDefinition> definitions)
    {
        foreach (var def in definitions)
        {
            _sources[def.Id] = def.Source;
            _states[def.Id] = new DataSourceState();
        }
    }

    public async Task<DataResult?> FetchAsync(string dataSourceId, DataQuery? query = null, CancellationToken ct = default)
    {
        if (!_sources.TryGetValue(dataSourceId, out var source))
            return null;

        var state = _states[dataSourceId];
        state.IsLoading = true;
        state.Error = null;

        for (int attempt = 0; attempt <= 3; attempt++)
        {
            try
            {
                var result = await source.FetchAsync(query, ct);
                state.IsLoading = false;
                state.LastData = result;
                state.RetryCount = 0;
                return result;
            }
            catch (Exception ex) when (attempt < 3)
            {
                state.RetryCount = attempt + 1;
                var delay = (int)Math.Pow(2, attempt) * 1000;
                await Task.Delay(delay, ct);
            }
            catch (Exception ex)
            {
                state.IsLoading = false;
                state.Error = ex.Message;
                return null;
            }
        }

        return null;
    }

    public async Task<ActionResult> SubmitAsync(string dataSourceId, string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (!_sources.TryGetValue(dataSourceId, out var source))
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
        foreach (var (id, source) in _sources)
        {
            if (!source.SupportsPush) continue;
            _subscriptions[id] = source.Subscribe(payload =>
            {
                OnDataChanged?.Invoke(id);
            });
        }
    }

    public DataSourceState? GetState(string dataSourceId)
        => _states.TryGetValue(dataSourceId, out var s) ? s : null;

    public void Dispose()
    {
        foreach (var sub in _subscriptions.Values)
            sub?.Dispose();
        _subscriptions.Clear();
        _sources.Clear();
        _states.Clear();
    }
}

public class DataSourceState
{
    public bool IsLoading { get; set; }
    public string? Error { get; set; }
    public DataResult? LastData { get; set; }
    public int RetryCount { get; set; }
}
