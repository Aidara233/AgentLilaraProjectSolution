// AgentCoreProcessor/Engine/Session/SessionTypeProfiles.cs
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 会话类型档案（唯一权限源）。一个 Storage/SessionTypes/{type}.json = 一个会话类型的完整描述：
/// Core / AllowedCores / Components / Tools（白黑名单）/ Prompts。
/// 取代旧 PermissionService + ComponentConfig：工具可见性裁决与组件装配清单都在这里，
/// 会话类型名即权限作用域（不再有 PermissionGroup 间接层）。
/// 文件缺失或损坏时回退到内置默认（DefaultFor 唯一种子），保证冷启动可用。
/// </summary>
internal static class SessionTypeProfiles
{
    private static string Dir => Path.Combine(PathConfig.StoragePath, "SessionTypes");

    private static readonly ConcurrentDictionary<string, SessionTypeProfile> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static SessionTypeProfile Get(string type)
    {
        if (string.IsNullOrEmpty(type)) type = "channel";
        if (_cache.TryGetValue(type, out var cached)) return cached;
        var profile = Load(type);
        _cache[type] = profile;
        return profile;
    }

    public static void Invalidate() => _cache.Clear();

    /// <summary>会话类型枚举：内置（channel/task/express/review）+ Storage/SessionTypes 下声明 Type 的类型档案。</summary>
    public static IReadOnlyList<string> All()
    {
        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "channel", "task", "express", "review" };
        if (Directory.Exists(Dir))
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            foreach (var f in Directory.GetFiles(Dir, "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                try
                {
                    var file = JsonSerializer.Deserialize<SessionProfileFile>(File.ReadAllText(f), opts);
                    if (file != null && !string.IsNullOrEmpty(file.Type)
                        && string.Equals(file.Type.Trim(), name, StringComparison.OrdinalIgnoreCase))
                        keys.Add(name);
                }
                catch { /* 坏文件跳过 */ }
            }
        }
        return keys.ToArray();
    }

    /// <summary>工具对会话类型是否可见（白/黑名单裁决）。type 为空 → 放行（不限制）。</summary>
    public static bool IsAllowed(string? type, string toolName)
    {
        if (string.IsNullOrEmpty(type)) return true;
        return Get(type).Tools.IsAllowed(toolName);
    }

    /// <summary>取某类型工具清单（白名单=允许项；黑名单=禁用项）。供 WebUI 展示。</summary>
    public static IReadOnlyList<string> GetTools(string type)
        => Get(type).Tools.List;

    /// <summary>取某类型权限模式（whitelist/blacklist）。</summary>
    public static string GetMode(string type)
        => Get(type).Tools.Mode;

    /// <summary>取某类型的默认（恢复默认用）。单一种子 = DefaultFor。</summary>
    public static (string Mode, string[] Tools) GetDefault(string type)
    {
        var seed = DefaultFor(type);
        return (seed.Tools.Mode, seed.Tools.List.ToArray());
    }

    /// <summary>保存某类型的工具模式与清单。仅改写文件内 Tools 段，保留其余字段。写后双缓存失效。</summary>
    public static void Save(string type, string mode, IEnumerable<string> tools)
    {
        var node = LoadOrCreateNode(type);
        node["Tools"] = new JsonObject
        {
            ["Mode"] = string.Equals(mode, "blacklist", StringComparison.OrdinalIgnoreCase) ? "blacklist" : "whitelist",
            ["List"] = new JsonArray(tools.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(t => (JsonNode)JsonValue.Create(t)!).ToArray())
        };
        Write(type, node);
    }

    /// <summary>保存某类型的组件清单。仅改写文件内 Components 段，保留其余字段。</summary>
    public static void SaveComponents(string type, IEnumerable<string> components)
    {
        var node = LoadOrCreateNode(type);
        node["Components"] = new JsonArray(components.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        Write(type, node);
    }

    private static JsonObject LoadOrCreateNode(string type)
    {
        var path = Path.Combine(Dir, $"{type}.json");
        if (File.Exists(path))
        {
            try
            {
                return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject) ?? new JsonObject();
            }
            catch { /* 坏文件：落到种子重建 */ }
        }
        var node = new JsonObject { ["Type"] = type };
        var seed = DefaultFor(type);
        node["Core"] = seed.DefaultCoreConfig;
        node["AllowedCores"] = new JsonArray(seed.AllowedCoreConfigs.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        node["Components"] = new JsonArray(seed.Components.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        if (seed.PromptFiles.Length > 0)
            node["Prompts"] = new JsonArray(seed.PromptFiles.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        node["Tools"] = new JsonObject
        {
            ["Mode"] = seed.Tools.Mode,
            ["List"] = new JsonArray(seed.Tools.List.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray())
        };
        return node;
    }

    private static void Write(string type, JsonObject node)
    {
        var path = Path.Combine(Dir, $"{type}.json");
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Invalidate();
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "会话类型配置保存失败", new { type, path, error = ex.Message });
        }
    }

    private static SessionTypeProfile Load(string type)
    {
        var path = Path.Combine(Dir, $"{type}.json");
        if (File.Exists(path))
        {
            try
            {
                var file = JsonSerializer.Deserialize<SessionProfileFile>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (file != null)
                    return Build(type, file);
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "会话类型配置加载失败，使用默认",
                    new { type, path, error = ex.Message });
            }
        }
        return DefaultFor(type);
    }

    private static SessionTypeProfile Build(string type, SessionProfileFile file)
    {
        var seed = DefaultFor(type);
        var defaultCore = string.IsNullOrEmpty(file.Core) ? seed.DefaultCoreConfig : file.Core;
        return new SessionTypeProfile
        {
            Type = type,
            DefaultCoreConfig = defaultCore,
            // 白名单缺省回退单核：仅默认核
            AllowedCoreConfigs = file.AllowedCores?.Length > 0
                ? file.AllowedCores : new[] { defaultCore },
            // 组件/提示词清单：文件显式声明则采用，缺省回退种子
            Components = file.Components?.Length > 0 ? file.Components : seed.Components,
            PromptFiles = file.Prompts?.Length > 0 ? file.Prompts : seed.PromptFiles,
            // 工具：文件声明 Tools 段则完整采用（含空清单），缺省回退种子
            Tools = file.Tools ?? seed.Tools
        };
    }

    /// <summary>内置默认（唯一种子）：与 templates/SessionTypes 一致，文件缺失时兜底。未知类型不在配置库中 → 抛异常。</summary>
    private static SessionTypeProfile DefaultFor(string type) => type switch
    {
        "channel" => new SessionTypeProfile
        {
            Type = type,
            DefaultCoreConfig = "ChannelCore",
            AllowedCoreConfigs = new[] { "ChannelCore", "ExpressCore" },
            Components = new[]
            {
                "basic-tools", "working-tools", "file-tools", "file-ops",
                "mind-tools", "system-ops", "network-tools"
            },
            PromptFiles = new[]
            {
                "identity.txt", "style.txt", "social.txt",
                "architecture_overview.txt", "tech_rules.txt", "channel_workflow.txt"
            },
            Tools = new SessionTools
            {
                Mode = "blacklist",
                List = new List<string>
                {
                    "escalate",
                    "review_list", "review_resolve", "review_preview", "review_commit", "review_evidence"
                }
            }
        },
        "task" => new SessionTypeProfile
        {
            Type = type,
            DefaultCoreConfig = "TaskCore",
            AllowedCoreConfigs = new[] { "TaskCore" },
            Components = new[]
            {
                "basic-tools", "file-tools", "mind-tools",
                "sub-agent-runtime", "network-tools"
            },
            PromptFiles = new[]
            {
                "identity.txt", "style.txt", "architecture_overview.txt",
                "tech_rules.txt", "task_workflow.txt", "SubAgentPrompt.txt"
            },
            Tools = new SessionTools
            {
                Mode = "whitelist",
                List = new List<string>
                {
                    "speak", "thinking", "send_media", "send_file", "adapter_action",
                    "list_channels", "search_chat_history",
                    "list_sessions", "send_session_message",
                    "read_text", "write_text", "list_dir", "move_file", "delete_file", "copy_file", "update_text",
                    "journal", "claim", "remember", "mind_status", "archive",
                    "report_to_parent",
                    "end_session", "wait", "compact", "connect_channel", "listen_channel",
                    "disconnect_channel", "get_image_text", "refine_image", "continue_loop",
                    "http_request", "download_file",
                    "pin_list", "pin_read", "pin_write", "pin_append", "pin_remove", "pin_search"
                }
            }
        },
        "express" => new SessionTypeProfile
        {
            Type = type,
            DefaultCoreConfig = "ExpressCore",
            AllowedCoreConfigs = new[] { "ExpressCore" },
            Components = new[] { "basic-tools" },
            PromptFiles = new[]
            {
                "identity.txt", "style.txt", "social.txt",
                "architecture_overview.txt", "tech_rules.txt", "express_workflow.txt"
            },
            Tools = new SessionTools
            {
                Mode = "whitelist",
                List = new List<string>
                {
                    "speak", "thinking", "send_media", "send_file", "adapter_action",
                    "list_channels", "search_chat_history",
                    "list_sessions", "send_session_message",
                    "escalate", "wait", "compact",
                    "connect_channel", "disconnect_channel",
                    "journal", "remember", "mind_status",
                    "pin_list", "pin_read", "pin_write", "pin_append", "pin_remove", "pin_search"
                }
            }
        },
        "review" => new SessionTypeProfile
        {
            Type = type,
            DefaultCoreConfig = "TaskCore",
            AllowedCoreConfigs = new[] { "TaskCore" },
            Components = new[]
            {
                "review-tools", "basic-tools", "file-tools", "mind-tools"
            },
            PromptFiles = new[] { "ReviewPrompt.txt", "mind_usage.txt" },
            Tools = new SessionTools
            {
                Mode = "whitelist",
                List = new List<string>
                {
                    "review_list", "review_resolve", "review_preview", "review_commit", "review_evidence",
                    "list_channels", "search_chat_history",
                    "list_sessions", "send_session_message",
                    "journal", "claim", "remember", "mind_status", "archive",
                    "end_session", "wait", "compact", "continue_loop",
                    "pin_list", "pin_read", "pin_write", "pin_append", "pin_remove", "pin_search"
                }
            }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type), type,
            $"会话类型不在配置库中：Storage/SessionTypes/{type}.json 不存在且无内置默认。可用类型：{string.Join(", ", All())}")
    };

    /// <summary>会话类型配置文件 DTO。Type 声明会话类型键（与文件名一致）；Core/AllowedCores/Components/Prompts/Tools 自包含。</summary>
    private sealed class SessionProfileFile
    {
        public string Type { get; set; } = "";
        public string Core { get; set; } = "";
        public string[] AllowedCores { get; set; } = Array.Empty<string>();
        public string[] Components { get; set; } = Array.Empty<string>();
        public string[] Prompts { get; set; } = Array.Empty<string>();
        public SessionTools? Tools { get; set; }
    }
}

/// <summary>会话类型运行时档案：Core 选择 + 组件清单 + 工具白黑名单。</summary>
internal sealed class SessionTypeProfile
{
    public string Type { get; set; } = "";
    public string DefaultCoreConfig { get; set; } = "";
    public string[] AllowedCoreConfigs { get; set; } = Array.Empty<string>();
    public string[] Components { get; set; } = Array.Empty<string>();
    public string[] PromptFiles { get; set; } = Array.Empty<string>();
    public SessionTools Tools { get; set; } = new();

    public bool IsToolAllowed(string toolName) => Tools.IsAllowed(toolName);
}

/// <summary>工具白黑名单：whitelist=仅清单内可用；blacklist=清单内禁用。</summary>
internal sealed class SessionTools
{
    public string Mode { get; set; } = "whitelist";

    public List<string> List { get; set; } = new();

    public bool IsWhitelist =>
        string.Equals(Mode, "whitelist", StringComparison.OrdinalIgnoreCase);

    public bool IsAllowed(string toolName)
    {
        var inList = List.Contains(toolName, StringComparer.OrdinalIgnoreCase);
        return IsWhitelist ? inList : !inList;
    }
}
