using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
            Meta = new PageMeta { Title = "测试页面", Icon = "bi-bug", Group = "测试", Order = 0, ShowInNav = false },
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
        },
        new()
        {
            Route = "test/cards",
            Meta = new PageMeta { Title = "卡片类型测试", Icon = "bi-grid", Group = "测试", Order = 1, ShowInNav = false },
            Cards = new List<CardDefinition>
            {
                new()
                {
                    Id = "test-form",
                    Type = CardType.Form,
                    DataSourceId = "test-form-data",
                    Title = "设置表单（测试）",
                    Schema = new FormSchema
                    {
                        ShowReset = true,
                        Groups = new()
                        {
                            new() { Name = "基本", Description = "基础参数配置" },
                            new() { Name = "高级", Description = "高级选项", DefaultCollapsed = true }
                        },
                        Fields = new()
                        {
                            new() { Field = "displayName", Label = "显示名称", Type = FormFieldType.Text,
                                    Placeholder = "请输入显示名称", Required = true, Group = "基本" },
                            new() { Field = "maxTokens", Label = "最大 Token 数", Type = FormFieldType.Number,
                                    Description = "单次调用允许的最大 token 数量", Group = "基本" },
                            new() { Field = "provider", Label = "模型提供商", Type = FormFieldType.Select,
                                    Group = "基本",
                                    Options = new()
                                    {
                                        new() { Value = "claude", Label = "Claude (Anthropic)" },
                                        new() { Value = "openai", Label = "OpenAI" },
                                        new() { Value = "local", Label = "本地模型" }
                                    }},
                            new() { Field = "enableCache", Label = "启用 Prompt 缓存", Type = FormFieldType.Toggle,
                                    Description = "开启后可降低重复内容的 token 消耗", Group = "高级" },
                            new() { Field = "systemPrompt", Label = "系统提示词", Type = FormFieldType.TextArea,
                                    Placeholder = "输入系统提示词…", Group = "高级" }
                        }
                    },
                    Layout = new CardLayout { PreferredCols = 6 }
                },
                new()
                {
                    Id = "test-detail",
                    Type = CardType.Detail,
                    DataSourceId = "test-detail-data",
                    Title = "实体详情（测试）",
                    Schema = new DetailSchema
                    {
                        Sections = new()
                        {
                            new()
                            {
                                Title = "基本信息",
                                Fields = new()
                                {
                                    new() { Field = "id", Label = "ID" },
                                    new() { Field = "name", Label = "名称", Editable = true },
                                    new() { Field = "status", Label = "状态", Format = ColumnFormat.Badge },
                                    new() { Field = "createdAt", Label = "创建时间", Format = ColumnFormat.DateTime }
                                }
                            },
                            new()
                            {
                                Title = "运行统计",
                                DefaultCollapsed = true,
                                Fields = new()
                                {
                                    new() { Field = "totalCalls", Label = "总调用次数" },
                                    new() { Field = "totalTokens", Label = "累计 Token" },
                                    new() { Field = "lastActive", Label = "最后活跃", Format = ColumnFormat.DateTime }
                                }
                            }
                        }
                    },
                    Layout = new CardLayout { PreferredCols = 6 }
                },
                new()
                {
                    Id = "test-stream",
                    Type = CardType.Stream,
                    DataSourceId = "test-stream-data",
                    Title = "日志流（测试）",
                    Schema = new StreamSchema
                    {
                        MaxLines = 200,
                        AutoScroll = true,
                        ShowPauseButton = true,
                        ShowFilter = true
                    },
                    Layout = new CardLayout { PreferredCols = 6 }
                },
                new()
                {
                    Id = "test-chat",
                    Type = CardType.Chat,
                    DataSourceId = "test-chat-data",
                    Title = "对话记录（测试）",
                    Schema = new ChatSchema
                    {
                        ShowSenderSwitch = true,
                        ShowInput = false,
                        Senders = new() { "用户", "Lilara" }
                    },
                    Layout = new CardLayout { PreferredCols = 6 }
                },
                new()
                {
                    Id = "test-tree",
                    Type = CardType.Tree,
                    DataSourceId = "test-tree-data",
                    Title = "目录树（测试）",
                    Schema = new TreeSchema
                    {
                        NodeIdField = "id",
                        NodeLabelField = "label",
                        ChildrenField = "children",
                        Expandable = true
                    },
                    Layout = new CardLayout { PreferredCols = 12 }
                },
                new()
                {
                    Id = "test-action-simple",
                    Type = CardType.Action,
                    DataSourceId = "test-action-simple",
                    Title = "动作卡片（无参）",
                    Schema = new ActionCardSchema
                    {
                        ActionId = "get_group_list",
                        ActionLabel = "获取群列表",
                        Description = "返回所有已加入的群。无需参数。",
                        SubmitLabel = "获取"
                    },
                    Layout = new CardLayout { PreferredCols = 4 }
                },
                new()
                {
                    Id = "test-action-params",
                    Type = CardType.Action,
                    DataSourceId = "test-action-params",
                    Title = "动作卡片（含参数）",
                    Schema = new ActionCardSchema
                    {
                        ActionId = "set_group_card",
                        ActionLabel = "设置群名片",
                        Description = "修改指定群成员的显示名片。",
                        Params = new()
                        {
                            new() { Name = "group_id", Label = "群号", Type = "text", Required = true },
                            new() { Name = "user_id", Label = "用户QQ", Type = "text", Required = true },
                            new() { Name = "card", Label = "新名片", Type = "text", Required = true }
                        }
                    },
                    Layout = new CardLayout { PreferredCols = 4 }
                },
                new()
                {
                    Id = "test-action-select",
                    Type = CardType.Action,
                    DataSourceId = "test-action-select",
                    Title = "动作卡片（下拉选择）",
                    Schema = new ActionCardSchema
                    {
                        ActionId = "send_template",
                        ActionLabel = "发送模板消息",
                        Description = "选择预设模板发送到指定频道。",
                        Params = new()
                        {
                            new() { Name = "template", Label = "模板", Type = "select", Required = true,
                                Options = new()
                                {
                                    new() { Value = "welcome", Label = "欢迎消息" },
                                    new() { Value = "help", Label = "帮助提示" },
                                    new() { Value = "goodbye", Label = "告别消息" }
                                }},
                            new() { Name = "channel", Label = "目标频道（可选）", Type = "text", Required = false }
                        },
                        SubmitLabel = "发送"
                    },
                    Layout = new CardLayout { PreferredCols = 4 }
                }
            },
            DataSources = new List<DataSourceDefinition>
            {
                new() { Id = "test-form-data",   Source = new TestFormDataSource() },
                new() { Id = "test-detail-data", Source = new TestDetailDataSource() },
                new() { Id = "test-stream-data", Source = new TestStreamDataSource() },
                new() { Id = "test-chat-data",   Source = new TestChatDataSource() },
                new() { Id = "test-tree-data",   Source = new TestTreeDataSource() },
                new() { Id = "test-action-simple", Source = new TestActionDataSource() },
                new() { Id = "test-action-params", Source = new TestActionDataSource() },
                new() { Id = "test-action-select", Source = new TestActionDataSource() }
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
        for (int i = 1; i <= 25; i++)
        {
            arr.Add(new JsonObject
            {
                ["id"] = i,
                ["name"] = $"测试项目 {i}",
                ["time"] = DateTime.Now.AddMinutes(-i * 10).ToString("O")
            });
        }
        return Task.FromResult(new DataResult { Data = arr, TotalCount = 25 });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

// --- 第二测试页数据源 ---

internal class TestFormDataSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var data = new JsonObject
        {
            ["displayName"] = "Lilara",
            ["maxTokens"] = 8192,
            ["provider"] = "claude",
            ["enableCache"] = true,
            ["systemPrompt"] = "你是 Lilara，一个有温度的 AI 助手。"
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true, Message = "设置已保存" });
}

internal class TestDetailDataSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var data = new JsonObject
        {
            ["id"] = "agent-001",
            ["name"] = "Lilara",
            ["status"] = "running",
            ["createdAt"] = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("O"),
            ["totalCalls"] = 4821,
            ["totalTokens"] = 2_340_000,
            ["lastActive"] = DateTime.UtcNow.AddMinutes(-3).ToString("O")
        };
        return Task.FromResult(new DataResult { Data = data });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class TestStreamDataSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var entries = new JsonArray
        {
            new JsonObject { ["time"] = now.AddSeconds(-30).ToString("HH:mm:ss"), ["level"] = "INFO",  ["text"] = "[ChannelEngine] 频道循环启动，channelId=qq-123456" },
            new JsonObject { ["time"] = now.AddSeconds(-25).ToString("HH:mm:ss"), ["level"] = "INFO",  ["text"] = "[MemoryService] 检索完成，命中 3 条记忆片段" },
            new JsonObject { ["time"] = now.AddSeconds(-20).ToString("HH:mm:ss"), ["level"] = "DEBUG", ["text"] = "[WorkingCore] 开始构建上下文，当前 token 估算 2048" },
            new JsonObject { ["time"] = now.AddSeconds(-15).ToString("HH:mm:ss"), ["level"] = "INFO",  ["text"] = "[WorkingCore] 模型调用完成，usage: input=2048 output=312 cache_hit=1820" },
            new JsonObject { ["time"] = now.AddSeconds(-10).ToString("HH:mm:ss"), ["level"] = "WARN",  ["text"] = "[TaskBridge] 委托队列积压 5 条，建议检查 SystemEngine 状态" },
            new JsonObject { ["time"] = now.AddSeconds(-5).ToString("HH:mm:ss"),  ["level"] = "INFO",  ["text"] = "[SystemEngine] 评估委托：accept，delegationId=d-0042" },
            new JsonObject { ["time"] = now.ToString("HH:mm:ss"),                 ["level"] = "ERROR", ["text"] = "[PluginLoader] 插件 Plugin.FileTools 加载失败：找不到依赖 SkiaSharp" }
        };
        return Task.FromResult(new DataResult { Data = entries, TotalCount = entries.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class TestChatDataSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var messages = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "m1",
                ["sender"] = "用户",
                ["role"] = "user",
                ["text"] = "今天天气怎么样？",
                ["time"] = now.AddMinutes(-5).ToString("O")
            },
            new JsonObject
            {
                ["id"] = "m2",
                ["sender"] = "Lilara",
                ["role"] = "assistant",
                ["text"] = "我没有实时天气数据，不过你可以查一下天气 App 哦。你在哪个城市？",
                ["time"] = now.AddMinutes(-4).ToString("O")
            },
            new JsonObject
            {
                ["id"] = "m3",
                ["sender"] = "用户",
                ["role"] = "user",
                ["text"] = "北京。",
                ["time"] = now.AddMinutes(-3).ToString("O")
            },
            new JsonObject
            {
                ["id"] = "m4",
                ["sender"] = "Lilara",
                ["role"] = "assistant",
                ["text"] = "北京这几天好像有沙尘，出门记得戴口罩～",
                ["time"] = now.AddMinutes(-2).ToString("O")
            }
        };
        return Task.FromResult(new DataResult { Data = messages, TotalCount = messages.Count });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class TestTreeDataSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        var tree = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "storage",
                ["label"] = "Storage/",
                ["children"] = new JsonArray
                {
                    new JsonObject { ["id"] = "storage-core", ["label"] = "Core/",
                        ["children"] = new JsonArray
                        {
                            new JsonObject { ["id"] = "core-claude", ["label"] = "ClaudeCore.json" },
                            new JsonObject { ["id"] = "core-vision", ["label"] = "VisionProvider.json" }
                        }
                    },
                    new JsonObject { ["id"] = "storage-engine", ["label"] = "Engine/",
                        ["children"] = new JsonArray
                        {
                            new JsonObject { ["id"] = "engine-perms", ["label"] = "ComponentConfig.json" }
                        }
                    },
                    new JsonObject { ["id"] = "storage-db", ["label"] = "agent.db" }
                }
            },
            new JsonObject
            {
                ["id"] = "plugins",
                ["label"] = "Plugins/",
                ["children"] = new JsonArray
                {
                    new JsonObject { ["id"] = "plugin-basic",   ["label"] = "Plugin.BasicTools.dll" },
                    new JsonObject { ["id"] = "plugin-memory",  ["label"] = "Plugin.MemoryTools.dll" },
                    new JsonObject { ["id"] = "plugin-file",    ["label"] = "Plugin.FileTools.dll" },
                    new JsonObject { ["id"] = "plugin-working", ["label"] = "Plugin.WorkingTools.dll" }
                }
            }
        };
        return Task.FromResult(new DataResult { Data = tree, TotalCount = 2 });
    }

    public Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
        => Task.FromResult(new ActionResult { Success = true });
}

internal class TestActionDataSource : IDataSource
{
    public bool SupportsPush => false;
    public IDisposable? Subscribe(Action<JsonNode?> callback) => null;

    private JsonNode? _lastData;

    public Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default)
    {
        return Task.FromResult(new DataResult { Data = _lastData! });
    }

    public async Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default)
    {
        await Task.Delay(800, ct);

        if (data == null)
            return new ActionResult { Success = false, Message = "缺少请求数据" };

        var actionId = data["actionId"]?.ToString() ?? "(未知)";
        var paramObj = data["params"] as JsonObject;
        var paramList = paramObj?.Select(kv => $"  {kv.Key} = {kv.Value}")
            .ToList() ?? new List<string>();

        var result = paramList.Count > 0
            ? $"[{DateTime.Now:HH:mm:ss}] 执行动作: {actionId}\n参数:\n{string.Join("\n", paramList)}\n\n✓ 执行成功 (mock)"
            : $"[{DateTime.Now:HH:mm:ss}] 执行动作: {actionId}\n\n✓ 执行成功 (mock)";

        _lastData = new JsonObject
        {
            ["_result"] = result
        };

        return new ActionResult { Success = true, Message = "执行完成" };
    }
}
