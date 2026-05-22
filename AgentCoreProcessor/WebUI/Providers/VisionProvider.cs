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
        Meta = new PageMeta { Title = "引擎状态", Icon = "bi-eye", Group = "视觉", Order = 40 },
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
                        new() { Field = "suspended", Label = "暂停", Type = StatusFieldType.Badge }
                    }
                },
                Layout = new CardLayout { PreferredCols = 6 }
            },
            new()
            {
                Id = "vision-config", Type = CardType.Status, DataSourceId = "vision-config", Title = "配置",
                Schema = new StatusSchema
                {
                    Fields = new()
                    {
                        new() { Field = "vision_enabled", Label = "识图启用", Type = StatusFieldType.Badge },
                        new() { Field = "ocr_enabled", Label = "OCR启用", Type = StatusFieldType.Badge },
                        new() { Field = "vision_concurrency", Label = "识图并发" },
                        new() { Field = "ocr_concurrency", Label = "OCR并发" },
                        new() { Field = "batch_size", Label = "批次大小" },
                        new() { Field = "ocr_threshold", Label = "OCR富文本阈值" },
                        new() { Field = "retry", Label = "重试" }
                    }
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
                        new() { Field = "ocr_pending", Label = "OCR待处理" },
                        new() { Field = "vision_pending", Label = "识图待处理" },
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
        Meta = new PageMeta { Title = "图片库", Icon = "bi-images", Group = "视觉", Order = 41 },
        Cards = new List<CardDefinition>
        {
            new()
            {
                Id = "gallery", Type = CardType.Table, DataSourceId = "gallery", Title = "图片库",
                Schema = new TableSchema
                {
                    Columns = new()
                    {
                        new() { Field = "time", Header = "时间", Width = "130px" },
                        new() { Field = "category", Header = "分类", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "ocr", Header = "OCR文本" },
                        new() { Field = "description", Header = "描述" },
                        new() { Field = "status", Header = "状态", Width = "80px", Format = ColumnFormat.Badge },
                        new() { Field = "size", Header = "大小", Width = "80px" }
                    },
                    DefaultPageSize = 30
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
                ["suspended"] = "—"
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
            ["suspended"] = snap.VisionSuspended ? $"是: {snap.SuspendReason}" : "否"
        };
        return Task.FromResult(new DataResult { Data = result });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
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
            ["vision_enabled"] = config.VisionEnabled ? "启用" : "禁用",
            ["ocr_enabled"] = config.OcrEnabled ? "启用" : "禁用",
            ["vision_concurrency"] = config.VisionConcurrency.ToString(),
            ["ocr_concurrency"] = config.OcrConcurrency.ToString(),
            ["batch_size"] = config.BatchSize.ToString(),
            ["ocr_threshold"] = $"{config.OcrRichTextThreshold} 字符",
            ["retry"] = $"{config.VisionRetryCount} 次 / {config.VisionRetryDelayMs}ms"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
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
            var status = img.Description != null ? "完成"
                : img.HasText != null ? "待识图"
                : "待OCR";

            arr.Add(new JsonObject
            {
                ["time"] = img.CreatedAt.ToString("MM-dd HH:mm"),
                ["category"] = img.Category ?? "—",
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

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}
