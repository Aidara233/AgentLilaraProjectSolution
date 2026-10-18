using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.WebUI.Shell;
using AgentLilara.PluginSDK.WebUI;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// WebUI 页面数据源生命周期与分页契约回归测试。
/// </summary>
public class WebDataSourceTests
{
    private sealed class FakeSource : IDataSource
    {
        public int FetchCount;
        public int SubmitCount;
        public Func<DataQuery?, CancellationToken, Task<DataResult>>? OnFetch;
        public bool SupportsPush { get; set; }
        public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

        public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref FetchCount);
            if (OnFetch != null) return await OnFetch(query, ct);
            return new DataResult { Data = new JsonArray() };
        }

        public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SubmitCount);
            return Task.FromResult(new ActionResult { Success = true, Message = "ok" });
        }
    }

    [Fact]
    public async Task FetchAsync_CancelledRequest_DoesNotStayLoading()
    {
        var source = new FakeSource
        {
            OnFetch = async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new DataResult { Data = new JsonArray() };
            }
        };
        var manager = new DataSourceManager();
        manager.Initialize(new[]
        {
            new DataSourceDefinition { Id = "ds", Source = source }
        });

        using var cts = new CancellationTokenSource();
        var fetch = manager.FetchAsync("ds", null, cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        var result = await fetch;

        Assert.Null(result);
        Assert.False(manager.GetState("ds")!.IsLoading);
        Assert.Null(manager.GetState("ds")!.Error);
    }

    [Fact]
    public async Task FetchAsync_NewerRequestWins_StaleResultIsDiscarded()
    {
        var gate = new TaskCompletionSource();
        var source = new FakeSource();
        var call = 0;
        source.OnFetch = async (q, ct) =>
        {
            var n = Interlocked.Increment(ref call);
            if (n == 1)
            {
                await gate.Task;
                return new DataResult { Data = JsonValue.Create("old") };
            }
            return new DataResult { Data = JsonValue.Create("new") };
        };

        var manager = new DataSourceManager();
        manager.Initialize(new[]
        {
            new DataSourceDefinition { Id = "ds", Source = source }
        });

        var first = manager.FetchAsync("ds", null);
        var second = manager.FetchAsync("ds", null);
        await second;
        gate.SetResult();
        await first;

        Assert.Equal("new", manager.GetState("ds")!.LastData!.Data!.GetValue<string>());
    }

    [Fact]
    public void Initialize_DuplicateIds_ReportsConfigurationError()
    {
        var manager = new DataSourceManager();
        manager.Initialize(new[]
        {
            new DataSourceDefinition { Id = "dup", Source = new FakeSource() },
            new DataSourceDefinition { Id = "dup", Source = new FakeSource() }
        });

        Assert.Contains("重复", manager.ConfigurationError);
        manager.Dispose();
    }

    [Fact]
    public void Factory_CreatesPerPageInstance()
    {
        var instances = new List<FakeSource>();
        var def = new DataSourceDefinition
        {
            Id = "ds",
            Factory = () =>
            {
                var s = new FakeSource();
                instances.Add(s);
                return s;
            }
        };

        var a = def.CreateSource();
        var b = def.CreateSource();
        Assert.NotSame(a, b);
        Assert.Equal(2, instances.Count);
    }

    [Fact]
    public async Task SubmitAsync_AfterDispose_ReturnsFailure()
    {
        var source = new FakeSource();
        var manager = new DataSourceManager();
        manager.Initialize(new[]
        {
            new DataSourceDefinition { Id = "ds", Source = source }
        });
        manager.Dispose();

        var result = await manager.SubmitAsync("ds", "save", new JsonObject());
        Assert.False(result.Success);
        Assert.Equal(0, source.SubmitCount);
    }

    [Fact]
    public void PagingMode_IsExplicitPerDefinition()
    {
        var manager = new DataSourceManager();
        manager.Initialize(new[]
        {
            new DataSourceDefinition { Id = "client", Source = new FakeSource() },
            new DataSourceDefinition { Id = "server", Source = new FakeSource(), Paging = DataPagingMode.Server }
        });

        Assert.Equal(DataPagingMode.Client, manager.GetPagingMode("client"));
        Assert.Equal(DataPagingMode.Server, manager.GetPagingMode("server"));
        manager.Dispose();
    }
}
