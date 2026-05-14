using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class TestProvider : IWebUIProvider
{
    public string Id => "test-provider";
    public string DisplayName => "测试 Provider";

    public IReadOnlyList<PageDefinition> Pages { get; } = new List<PageDefinition>
    {
        new()
        {
            Route = "test/status",
            Meta = new PageMeta { Title = "测试页面", Icon = "bi-bug", Group = "测试", Order = 0 },
            Cards = new List<CardDefinition>
            {
                new()
                {
                    Id = "test-status",
                    Type = CardType.Status,
                    DataSourceId = "test-data",
                    Title = "系统状态（测试）",
                    Schema = new StatusSchema
                    {
                        Fields = new()
                        {
                            new() { Field = "status", Label = "状态", Type = StatusFieldType.Indicator },
                            new() { Field = "uptime", Label = "运行时间" },
                            new() { Field = "version", Label = "版本", Type = StatusFieldType.Badge }
                        },
                        Actions = new()
                        {
                            new() { Id = "refresh", Label = "刷新", Icon = "bi-arrow-clockwise" }
                        }
                    },
                    Layout = new CardLayout { PreferredCols = 6 }
                },
                new()
                {
                    Id = "test-table",
                    Type = CardType.Table,
                    DataSourceId = "test-list",
                    Title = "测试列表",
                    Schema = new TableSchema
                    {
                        Columns = new()
                        {
                            new() { Field = "id", Header = "ID", Width = "60px" },
                            new() { Field = "name", Header = "名称" },
                            new() { Field = "time", Header = "时间", Format = ColumnFormat.DateTime }
                        }
                    },
                    Layout = new CardLayout { PreferredCols = 6 }
                }
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = "test-data", Source = new TestStatusDataSource() },
                new() { Id = "test-list", Source = new TestListDataSource() }
            }
        }
    };
}

internal class TestStatusDataSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var data = new JsonObject
        {
            ["status"] = "running",
            ["uptime"] = $"{(int)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMinutes} 分钟",
            ["version"] = "1.0.0"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true, Message = "OK" });
}

internal class TestListDataSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var arr = new JsonArray();
        for (int i = 1; i <= 5; i++)
        {
            arr.Add(new JsonObject
            {
                ["id"] = i,
                ["name"] = $"测试项目 {i}",
                ["time"] = DateTime.Now.AddMinutes(-i * 10).ToString("O")
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = 5 });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
