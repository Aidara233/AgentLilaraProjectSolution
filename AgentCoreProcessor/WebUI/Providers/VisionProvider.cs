using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Vision;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.WebUI;

using ImageStorage = AgentCoreProcessor.Adapter.ImageStorage;

namespace AgentCoreProcessor.WebUI.Providers;

[WebUIProvider(BuiltIn = true)]
internal class VisionProvider : IWebUIProvider
{
    public string Id => "core-vision";
    public string DisplayName => "视觉";

    private readonly MasterEngine _engine;

    public VisionProvider(MasterEngine engine)
    {
        _engine = engine;
    }

    public IReadOnlyList<PageDefinition> Pages => new List<PageDefinition>
    {
        BuildStatusPage(),
        BuildGalleryPage()
    };

    private PageDefinition BuildStatusPage() => new()
    {
        Route = "vision",
        Meta = new PageMeta { Title = "引擎状态", Icon = "bi-eye", Group = "视觉引擎", Order = 70 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "vision-status", Type = CardType.Status, DataSourceId = "vision-status", Title = "视觉引擎",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "state", Label = "状态", Type = StatusFieldType.Indicator },
                        new() { Field = "active", Label = "并发任务" },
                        new() { Field = "processed", Label = "已处理" },
                        new() { Field = "vision_status", Label = "识图", Type = StatusFieldType.Badge },
                        new() { Field = "ocr_status", Label = "OCR", Type = StatusFieldType.Badge },
                        new() { Field = "vision_errors", Label = "识图错误" },
                        new() { Field = "ocr_errors", Label = "OCR错误" },
                        new() { Field = "phase1", Label = "Phase1" },
                        new() { Field = "phase2", Label = "Phase2" },
                        new() { Field = "phase3", Label = "Phase3" },
                        new() { Field = "suspended", Label = "暂停", Type = StatusFieldType.Badge }
                    },
                    Actions = new()
                    {
                        new() { Id = "start", Label = "启动引擎", Confirm = "" },
                        new() { Id = "wake", Label = "立即处理", Confirm = "" },
                        new() { Id = "stop", Label = "停止引擎", Danger = true, Confirm = "确认停止视觉引擎？下次心跳会自动重启。" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "vision-config", Type = CardType.Form, DataSourceId = "vision-config", Title = "配置",
                Schema = new FormSchema
                {
                    ShowSubmit = true,
                    ShowReset = false,
                    Fields = new()
                    {
                        new() { Field = "visionEnabled", Label = "识图启用", Type = FormFieldType.Toggle },
                        new() { Field = "ocrEnabled", Label = "OCR启用", Type = FormFieldType.Toggle },
                        new() { Field = "phase1Concurrency", Label = "P1并发", Type = FormFieldType.Number },
                        new() { Field = "phase2Concurrency", Label = "P2并发", Type = FormFieldType.Number },
                        new() { Field = "ocrConcurrency", Label = "OCR并发", Type = FormFieldType.Number },
                        new() { Field = "batchSize", Label = "批次大小", Type = FormFieldType.Number },
                        new() { Field = "refineTriggerCount", Label = "精炼触发数(条)", Type = FormFieldType.Number },
                        new() { Field = "ocrRichTextThreshold", Label = "OCR富文本阈值", Type = FormFieldType.Number },
                    },
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "vision-queue", Type = CardType.Status, DataSourceId = "vision-queue", Title = "队列",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "ocr_pending", Label = "待OCR" },
                        new() { Field = "vision_pending", Label = "待Phase1" },
                        new() { Field = "total_images", Label = "图片总数" }
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "vision-status", Source = new VisionStatusSource(_engine) },
            new() { Id = "vision-config", Source = new VisionConfigSource(_engine) },
            new() { Id = "vision-queue", Source = new VisionQueueSource() }
        }
    };

    private PageDefinition BuildGalleryPage() => new()
    {
        Route = "vision/gallery",
        Meta = new PageMeta { Title = "图片库", Icon = "bi-images", Group = "视觉引擎", Order = 71 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "gallery", Type = CardType.Table, DataSourceId = "gallery", Title = "图片库",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "thumbnail", Header = "预览", Width = "96px", Format = ColumnFormat.Image, Sortable = false },
                        new() { Field = "time", Header = "时间", Width = "110px" },
                        new() { Field = "category", Header = "分类", Width = "70px", Format = ColumnFormat.Badge },
                        new() { Field = "phase", Header = "阶段", Width = "55px", Format = ColumnFormat.Badge },
                        new() { Field = "ocr", Header = "OCR文本" },
                        new() { Field = "description", Header = "描述" },
                        new() { Field = "status", Header = "状态", Width = "70px", Format = ColumnFormat.Badge },
                        new() { Field = "size", Header = "大小", Width = "70px" }
                    },
                    DefaultPageSize = 30,
                    RowActions = new()
                    {
                        new() { Id = "retry-ocr", Label = "重OCR" },
                        new() { Id = "retry-vision", Label = "重识图" },
                        new() { Id = "delete", Label = "删除", Danger = true, Confirm = "确认删除此图片？" },
                    }
                },
                Layout = new CardLayout { PreferredCols = 12 }
            }
        },
        DataSources = new List<DataSourceDefinition>
        {
            new() { Id = "gallery", Source = new VisionGallerySource() }
        }
    };
}

// ---- 数据源 ----

internal class VisionStatusSource : IDataSource
{
    private readonly MasterEngine _engine;
    public VisionStatusSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 5000, 5000);
        return new TimerDisposable(timer);
    }

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<VisionEngineSpawnCheck>();
        var snap = check?.ActiveInstance?.GetSnapshot();

        if (snap == null)
        {
            var data = new JsonObject
            {
                ["state"] = "未启动",
                ["active"] = "—",
                ["processed"] = "—",
                ["vision_status"] = "—",
                ["ocr_status"] = "—",
                ["vision_errors"] = "—",
                ["ocr_errors"] = "—",
                ["suspended"] = "—",
                ["phase1"] = "—",
                ["phase2"] = "—",
                ["phase3"] = "—",
                ["_disabled_actions"] = new JsonArray("wake", "stop")
            };
            return Task.FromResult(new DataResult { Data = data });
        }

        var result = new JsonObject
        {
            ["state"] = snap.IsBusy ? "处理中" : "空闲",
            ["active"] = $"{snap.ActiveTasks} 个",
            ["processed"] = $"{snap.TotalProcessed} 张",
            ["vision_status"] = snap.VisionAvailable ? "可用" : "未配置",
            ["ocr_status"] = snap.OcrAvailable ? "可用" : "未配置",
            ["vision_errors"] = snap.VisionErrors.ToString(),
            ["ocr_errors"] = snap.OcrErrors.ToString(),
            ["phase1"] = snap.Phase1Count.ToString(),
            ["phase2"] = snap.Phase2Count.ToString(),
            ["phase3"] = snap.Phase3Count.ToString(),
            ["suspended"] = snap.VisionSuspended ? $"是: {snap.SuspendReason}" : "否",
            ["_disabled_actions"] = new JsonArray("start")
        };
        return Task.FromResult(new DataResult { Data = result });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<VisionEngineSpawnCheck>();
        var instance = check?.ActiveInstance;

        switch (action)
        {
            case "start":
            {
                var spawnCheck = _engine.GetSpawnCheck<VisionEngineSpawnCheck>();
                if (spawnCheck == null)
                    return Task.FromResult(new ActionResult { Success = false, Message = "SpawnCheck 未注册" });
                var newEngine = spawnCheck.Create(_engine);
                _engine.StartEngine(newEngine);
                return Task.FromResult(new ActionResult { Success = true, Message = "视觉引擎已启动" });
            }
            case "wake":
                if (instance == null)
                    return Task.FromResult(new ActionResult { Success = false, Message = "引擎未运行" });
                instance.SignalGate();
                return Task.FromResult(new ActionResult { Success = true, Message = "已触发立即处理" });
            case "stop":
                if (instance == null)
                    return Task.FromResult(new ActionResult { Success = false, Message = "引擎未运行" });
                instance.RequestStop();
                return Task.FromResult(new ActionResult { Success = true, Message = "已请求停止，下次心跳会自动重启" });
            default:
                return Task.FromResult(new ActionResult { Success = false, Message = $"未知操作: {action}" });
        }
    }
}

internal class VisionConfigSource : IDataSource
{
    private readonly MasterEngine _engine;
    public VisionConfigSource(MasterEngine engine) => _engine = engine;
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<VisionEngineSpawnCheck>();
        var snap = check?.ActiveInstance?.GetSnapshot();
        var config = snap?.Config ?? VisionEngineConfig.Load();

        var data = new JsonObject
        {
            ["visionEnabled"] = config.VisionEnabled,
            ["ocrEnabled"] = config.OcrEnabled,
            ["phase1Concurrency"] = config.Phase1Concurrency,
            ["phase2Concurrency"] = config.Phase2Concurrency,
            ["ocrConcurrency"] = config.OcrConcurrency,
            ["batchSize"] = config.BatchSize,
            ["refineTriggerCount"] = config.RefineTriggerCount,
            ["ocrRichTextThreshold"] = config.OcrRichTextThreshold,
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        var check = _engine.GetSpawnCheck<VisionEngineSpawnCheck>();
        var instance = check?.ActiveInstance;
        var config = instance?.GetSnapshot()?.Config ?? VisionEngineConfig.Load();

        // 从表单数据更新配置
        if (data != null)
        {
            config.VisionEnabled = string.Equals(data["visionEnabled"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            config.OcrEnabled = string.Equals(data["ocrEnabled"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase);
            if (int.TryParse(data["phase1Concurrency"]?.ToString(), out var p1c)) config.Phase1Concurrency = p1c;
            if (int.TryParse(data["phase2Concurrency"]?.ToString(), out var p2c)) config.Phase2Concurrency = p2c;
            if (int.TryParse(data["ocrConcurrency"]?.ToString(), out var oc)) config.OcrConcurrency = oc;
            if (int.TryParse(data["batchSize"]?.ToString(), out var bs)) config.BatchSize = bs;
            if (int.TryParse(data["refineTriggerCount"]?.ToString(), out var rt)) config.RefineTriggerCount = rt;
            if (int.TryParse(data["ocrRichTextThreshold"]?.ToString(), out var ott)) config.OcrRichTextThreshold = ott;
        }

        config.Save();
        instance?.UpdateConfig(config);
        return Task.FromResult(new ActionResult { Success = true, Message = "配置已保存并生效" });
    }
}

internal class VisionQueueSource : IDataSource
{
    public bool SupportsPush => true;

    public IDisposable? Subscribe(Action<JsonNode?> callback)
    {
        var timer = new System.Threading.Timer(_ => callback(null), null, 10000, 10000);
        return new TimerDisposable(timer);
    }

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var ocrPending = await ImageStorage.GetOcrPendingAsync(50);
        var visionPending = await ImageStorage.GetVisionPendingAsync(50);
        var totalCount = await ImageStorage.GetFilteredCountAsync(null, null, null);

        var data = new JsonObject
        {
            ["ocr_pending"] = ocrPending.Count > 0 ? $"{ocrPending.Count} 张" : "无",
            ["vision_pending"] = visionPending.Count > 0 ? $"{visionPending.Count} 张" : "无",
            ["total_images"] = $"{totalCount} 张"
        };
        return new DataResult { Data = data };
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class VisionGallerySource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public async Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var page = query?.Page ?? 1;
        var pageSize = query?.PageSize ?? 30;
        var offset = (page - 1) * pageSize;
        var keyword = query?.Search;

        var images = await ImageStorage.GetPagedAsync(offset, pageSize, null, null, keyword);
        var total = await ImageStorage.GetFilteredCountAsync(null, null, keyword);

        var arr = new JsonArray();
        foreach (var img in images)
        {
            var status = img.Phase switch
            {
                0 => img.HasText != null ? "待粗扫" : "待OCR",
                1 => "已粗扫",
                2 => "已精炼",
                3 => "已强制",
                _ => "完成"
            };

            var phaseLabel = img.Phase switch
            {
                0 => "—",
                1 => "P1",
                2 => "P2",
                3 => "P3",
                _ => "?"
            };

            var thumbUrl = $"/images/thumbs/{img.Hash}.jpg";

            arr.Add(new JsonObject
            {
                ["id"] = img.Id,
                ["thumbnail"] = thumbUrl,
                ["_img_src"] = thumbUrl,
                ["time"] = img.CreatedAt.ToString("MM-dd HH:mm"),
                ["category"] = img.Classification ?? img.Category ?? "—",
                ["phase"] = phaseLabel,
                ["ocr"] = Truncate(img.OcrText, 80),
                ["description"] = Truncate(img.Description, 100),
                ["status"] = status,
                ["size"] = FormatSize(img.FileSize)
            });
        }
        return new DataResult { Data = arr, TotalCount = total };
    }

    private static string Truncate(string? s, int max)
        => s == null ? "—" : s.Length <= max ? s : s[..max] + "...";

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024}KB";
        return $"{bytes / (1024 * 1024.0):F1}MB";
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        if (data?["id"]?.GetValue<int>() is not int id || id <= 0)
            return new ActionResult { Success = false, Message = "无效的图片ID" };

        switch (action)
        {
            case "delete":
                var img = await ImageStorage.GetByIdAsync(id);
                if (img == null) return new ActionResult { Success = false, Message = "图片不存在" };
                await ImageStorage.DeleteAsync(img.Hash);
                return new ActionResult { Success = true, Message = "已删除" };
            case "retry-ocr":
                await ImageStorage.ResetOcrAsync(id);
                return new ActionResult { Success = true, Message = "已重置OCR状态，将重新处理" };
            case "retry-vision":
                await ImageStorage.ResetVisionAsync(id);
                return new ActionResult { Success = true, Message = "已重置识图状态，将重新处理" };
            default:
                return new ActionResult { Success = false, Message = $"未知操作: {action}" };
        }
    }
}
