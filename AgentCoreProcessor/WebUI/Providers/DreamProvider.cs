using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK.WebUI;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class DreamProvider : IWebUIProvider
{
    public string Id => "core-dream";
    public string DisplayName => "做梦";

    private readonly MasterEngine _engine;

    public DreamProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages => new List<PageDefinition>
    {
        BuildStatusPage(),
        BuildConfigPage()
    };

    // ---- 状态页 ----

    private PageDefinition BuildStatusPage() => new()
    {
        Route = "dream",
        Meta = new PageMeta { Title = "状态", Icon = "bi-moon-stars", Group = "做梦引擎", Order = 50 },
        LayoutType = PageLayoutType.Sidebar,
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "dream-status", Type = CardType.Status, DataSourceId = "dream-status", Title = "引擎状态",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "state", Label = "状态", Type = StatusFieldType.Indicator },
                        new() { Field = "phase", Label = "当前阶段" },
                        new() { Field = "steps", Label = "本轮进度" },
                        new() { Field = "temp_total", Label = "临时记忆" },
                        new() { Field = "temp_hot", Label = "热 (≥0.5)" },
                        new() { Field = "temp_cold", Label = "冷 (<0.15)" },
                        new() { Field = "temp_avg", Label = "平均热度" }
                    },
                    Actions = new()
                    {
                        new() { Id = "force-sleep", Label = "手动触发维护", Icon = "bi-moon" },
                        new() { Id = "force-review-beacon", Label = "信标触发 Review", Icon = "bi-flag" },
                        new() { Id = "force-review-candidate", Label = "候选人触发 Review", Icon = "bi-person-check" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6, GridColumnStart = 1 }
            },
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "dream-status", Source = new DreamStatusSource(_engine) }
        }
    };

    // ---- 配置页 ----

    private PageDefinition BuildConfigPage() => new()
    {
        Route = "dream/config",
        Meta = new PageMeta { Title = "配置", Icon = "bi-sliders", Group = "做梦引擎", Order = 51 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "dream-config", Type = CardType.Form, DataSourceId = "dream-config", Title = "维护参数",
                Schema = new FormSchema
                {
                    ShowSubmit = true,
                    Fields = new()
                    {
                        new() { Field = "EmbedParallelLimit", Label = "Embed 并行数", Type = FormFieldType.Number },
                        new() { Field = "MaxPatrolSteps", Label = "巡逻步数/轮", Type = FormFieldType.Number },
                        new() { Field = "OrderClassifyMinCos", Label = "秩序分类最小余弦", Type = FormFieldType.Number },
                        new() { Field = "OrderMergeMinSupport", Label = "自动合并阈值", Type = FormFieldType.Number },
                        new() { Field = "TriangleClassifyMinCos", Label = "三角闭合最小余弦", Type = FormFieldType.Number },
                        new() { Field = "TriangleBufferSize", Label = "三角缓冲大小", Type = FormFieldType.Number },
                        new() { Field = "RelationBatchMaxTargets", Label = "LLM 分类批大小", Type = FormFieldType.Number },
                        new() { Field = "DecayThreshold", Label = "衰减删除阈值", Type = FormFieldType.Number },
                        new() { Field = "ColdStartPoolSize", Label = "冷启动池大小", Type = FormFieldType.Number },
                        new() { Field = "ReviewIntervalHours", Label = "Review 间隔(小时)", Type = FormFieldType.Number },
                    }
                },
                Layout = new CardLayout { PreferredCols = 6, GridColumnStart = 1 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "dream-config", Source = new DreamConfigSource(_engine) }
        }
    };
}

// ======== 数据源 ========

internal class DreamStatusSource : IDataSource
{
    private readonly MasterEngine _engine;
    public DreamStatusSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new Timer(_ => callback(null), null, 3000, 3000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var hasActive = _engine.HasActiveEngine("Dream");
        var check = _engine.GetSpawnCheck<DreamEngineSpawnCheck>();
        var snap = check?.GetDreamSnapshot(hasActive);

        if (!hasActive)
        {
            var idle = new JsonObject
            {
                ["state"] = "空闲",
                ["phase"] = "—",
                ["steps"] = "—",
                ["temp_total"] = "—",
                ["temp_hot"] = "—",
                ["temp_cold"] = "—",
                ["temp_avg"] = "—"
            };

            // 即使空闲也查 temp 统计
            try
            {
                var temps = await _engine.TempMemories.GetAllAsync();
                if (temps.Count > 0)
                {
                    float avgHeat = temps.Average(t => t.Heat);
                    idle["temp_total"] = $"{temps.Count} 条";
                    idle["temp_hot"] = $"{temps.Count(t => t.Heat >= 0.5f)} 条";
                    idle["temp_cold"] = $"{temps.Count(t => t.Heat < 0.15f)} 条";
                    idle["temp_avg"] = $"{avgHeat:F3}";
                }
            }
            catch { }

            return new DataResult { Data = idle };
        }

        // 活跃状态
        var phase = snap?.CurrentFragment ?? "维护中";
        var steps = snap?.FragmentsTotal > 0
            ? $"{snap!.FragmentsCompleted}/{snap.FragmentsTotal}"
            : "—";

        var data = new JsonObject
        {
            ["state"] = "运行中",
            ["phase"] = phase,
            ["steps"] = steps
        };

        try
        {
            var temps = await _engine.TempMemories.GetAllAsync();
            if (temps.Count > 0)
            {
                float avgHeat = temps.Average(t => t.Heat);
                data["temp_total"] = $"{temps.Count} 条";
                data["temp_hot"] = $"{temps.Count(t => t.Heat >= 0.5f)} 条";
                data["temp_cold"] = $"{temps.Count(t => t.Heat < 0.15f)} 条";
                data["temp_avg"] = $"{avgHeat:F3}";
            }
            else
            {
                data["temp_total"] = "0";
                data["temp_hot"] = "0";
                data["temp_cold"] = "0";
                data["temp_avg"] = "0";
            }
        }
        catch
        {
            data["temp_total"] = "—";
            data["temp_hot"] = "—";
            data["temp_cold"] = "—";
            data["temp_avg"] = "—";
        }

        return new DataResult { Data = data };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action == "force-sleep")
        {
            _engine.EventBus.PublishSignal("force-sleep", "deepsleep");
            return Task.FromResult(new ActionResult { Success = true, Message = "已发送维护触发信号" });
        }
        if (action == "force-review-beacon")
        {
            _engine.EventBus.PublishSignal("force-review:beacon", "");
            return Task.FromResult(new ActionResult { Success = true, Message = "已触发信标 Review" });
        }
        if (action == "force-review-candidate")
        {
            _engine.EventBus.PublishSignal("force-review:candidate", "");
            return Task.FromResult(new ActionResult { Success = true, Message = "已触发候选人 Review" });
        }
        return Task.FromResult(new ActionResult { Success = true });
    }
}

internal class DreamConfigSource : IDataSource
{
    private readonly MasterEngine _engine;
    private static string ConfigPath => Path.Combine(PathConfig.StoragePath, "Dream", "DreamConfig.json");

    public DreamConfigSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<DreamEngineSpawnCheck>();
        var config = check?.GetConfig() ?? DreamConfig.Load(ConfigPath);

        var data = new JsonObject
        {
            ["EmbedParallelLimit"] = config.EmbedParallelLimit,
            ["MaxPatrolSteps"] = config.MaxPatrolSteps,
            ["OrderClassifyMinCos"] = config.OrderClassifyMinCos,
            ["OrderMergeMinSupport"] = config.OrderMergeMinSupport,
            ["TriangleClassifyMinCos"] = config.TriangleClassifyMinCos,
            ["TriangleBufferSize"] = config.TriangleBufferSize,
            ["RelationBatchMaxTargets"] = config.RelationBatchMaxTargets,
            ["DecayThreshold"] = config.DecayThreshold,
            ["ColdStartPoolSize"] = config.ColdStartPoolSize,
            ["ReviewIntervalHours"] = config.ReviewIntervalHours,
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (action != "save" || data is not JsonObject payload)
            return Task.FromResult(new ActionResult { Success = false, Message = "无效请求" });

        try
        {
            var config = DreamConfig.Load(ConfigPath);

            foreach (var (key, value) in payload)
            {
                var v = value?.ToString() ?? "";
                switch (key)
                {
                    case "EmbedParallelLimit": if (int.TryParse(v, out var el)) config.EmbedParallelLimit = el; break;
                    case "MaxPatrolSteps": if (int.TryParse(v, out var ms)) config.MaxPatrolSteps = ms; break;
                    case "OrderClassifyMinCos": if (float.TryParse(v, out var oc)) config.OrderClassifyMinCos = oc; break;
                    case "OrderMergeMinSupport": if (float.TryParse(v, out var om)) config.OrderMergeMinSupport = om; break;
                    case "TriangleClassifyMinCos": if (float.TryParse(v, out var tc)) config.TriangleClassifyMinCos = tc; break;
                    case "TriangleBufferSize": if (int.TryParse(v, out var tb)) config.TriangleBufferSize = tb; break;
                    case "RelationBatchMaxTargets": if (int.TryParse(v, out var rb)) config.RelationBatchMaxTargets = rb; break;
                    case "DecayThreshold": if (float.TryParse(v, out var dt)) config.DecayThreshold = dt; break;
                    case "ColdStartPoolSize": if (int.TryParse(v, out var cp)) config.ColdStartPoolSize = cp; break;
                    case "ReviewIntervalHours": if (int.TryParse(v, out var ri)) config.ReviewIntervalHours = ri; break;
                }
            }

            config.Save(ConfigPath);
            _engine.EventBus.PublishSignal("dream-config",
                System.Text.Json.JsonSerializer.Serialize(config));
            return Task.FromResult(new ActionResult { Success = true, Message = "配置已保存" });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ActionResult { Success = false, Message = $"保存失败: {ex.Message}" });
        }
    }
}
