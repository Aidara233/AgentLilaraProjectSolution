// AgentCoreProcessor/Engine/Session/SessionTypeProfiles.cs
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 会话类型档案加载器（会话类型配置库）。从 Storage/Session/{type}.json 读取（channel.json / task.json / review.json ...），
/// 类型键为字符串，任意新类型只需新增一个 JSON 文件，无需改代码。缓存 + Invalidate。
/// 文件缺失或损坏时回退到内置默认（与种子一致），保证冷启动可用。
/// </summary>
internal static class SessionTypeProfiles
{
    private static string Dir => Path.Combine(PathConfig.StoragePath, "Session");

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

    /// <summary>会话类型配置库枚举：内置默认（channel/task）+ Storage/Session 目录下声明 Type 的类型档案。
    /// 纯权限文件（express.json 无 Type 字段）不计数。</summary>
    public static IReadOnlyList<string> All()
    {
        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "channel", "task", "express" };
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
                {
                    var defaultCore = string.IsNullOrEmpty(file.DefaultCoreConfig)
                        ? DefaultFor(type).DefaultCoreConfig : file.DefaultCoreConfig;
                    return new SessionTypeProfile
                    {
                        Type = type,
                        DefaultCoreConfig = defaultCore,
                        // 白名单缺省回退单核：仅默认核
                        AllowedCoreConfigs = file.AllowedCores?.Length > 0
                            ? file.AllowedCores : new[] { defaultCore },
                        PromptFiles = file.PromptFiles?.Length > 0
                            ? file.PromptFiles : DefaultFor(type).PromptFiles,
                        DefaultComponentIds = file.DefaultComponentIds?.Length > 0
                            ? file.DefaultComponentIds : DefaultFor(type).DefaultComponentIds,
                        PermissionGroup = string.IsNullOrEmpty(file.PermissionGroup)
                            ? type : file.PermissionGroup,
                        PermissionMode = string.IsNullOrEmpty(file.PermissionMode)
                            ? "whitelist" : file.PermissionMode,
                        PermissionTools = file.PermissionTools ?? Array.Empty<string>()
                    };
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "会话类型配置加载失败，使用默认",
                    new { type, path, error = ex.Message });
            }
        }
        return DefaultFor(type);
    }

    /// <summary>内置默认：与 Storage/Session 种子一致，文件缺失时兜底。未知类型不在配置库中 → 抛异常。</summary>
    private static SessionTypeProfile DefaultFor(string type) => type switch
    {
        "channel" => new SessionTypeProfile
        {
            Type = type,
            DefaultCoreConfig = "ChannelCore",
            AllowedCoreConfigs = new[] { "ChannelCore" },
            PromptFiles = new[]
            {
                "identity.txt", "style.txt", "social.txt",
                "architecture_overview.txt", "tech_rules.txt", "channel_workflow.txt"
            },
            DefaultComponentIds = new[]
            {
                "basic-tools", "working-tools", "file-tools", "file-ops",
                "mind-tools", "system-ops"
            },
            PermissionGroup = "channel",
            PermissionMode = "blacklist",
            PermissionTools = new[] { "escalate" }.Concat(ReviewToolSeed).ToArray()
        },
        "task" => new SessionTypeProfile
        {
            Type = type,
            DefaultCoreConfig = "TaskCore",
            AllowedCoreConfigs = new[] { "TaskCore" },
            PromptFiles = new[]
            {
                "identity.txt", "style.txt", "architecture_overview.txt",
                "tech_rules.txt", "task_workflow.txt", "SubAgentPrompt.txt"
            },
            DefaultComponentIds = new[]
            {
                "basic-tools", "file-tools", "mind-tools",
                "sub-agent-runtime"
            },
            PermissionGroup = "task",
            PermissionMode = "whitelist",
            PermissionTools = TaskToolSeed
        },
        "express" => new SessionTypeProfile
        {
            Type = type,
            DefaultCoreConfig = "ExpressCore",
            AllowedCoreConfigs = new[] { "ExpressCore" },
            PromptFiles = new[]
            {
                "identity.txt", "style.txt", "social.txt",
                "architecture_overview.txt", "tech_rules.txt", "express_workflow.txt"
            },
            DefaultComponentIds = new[]
            {
                "basic-tools"
            },
            PermissionGroup = "express",
            PermissionMode = "whitelist",
            PermissionTools = new[]
            {
                "speak", "thinking", "send_media", "send_file", "adapter_action",
                "escalate", "wait",
                "connect_channel", "disconnect_channel",
                "journal", "remember", "mind_status"
            }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type), type,
            $"会话类型不在配置库中：Storage/Session/{type}.json 不存在且无内置默认。可用类型：{string.Join(", ", All())}")
    };

    /// <summary>task 组白名单种子：Task profile 组件工具 + 核心 session 工具（不含全局工具）。</summary>
    internal static readonly string[] TaskToolSeed =
    {
        // basic-tools
        "speak", "thinking", "send_media", "send_file", "adapter_action",
        // file-tools
        "read_text", "write_text", "list_dir", "move_file", "delete_file", "copy_file", "update_text",
        // mind-tools（心智工具）
        "journal", "claim", "remember", "mind_status", "archive",
        // sub-agent-runtime
        "report_to_parent",
        // 核心 session 工具
        "end_session", "wait", "compact", "connect_channel", "listen_channel",
        "disconnect_channel", "get_image_text", "refine_image", "continue_loop"
    };

    /// <summary>review 工具集：channel 黑名单与 review 白名单共用（Working 会话不可用复盘工具）。</summary>
    internal static readonly string[] ReviewToolSeed =
    {
        "review_list", "review_resolve", "review_preview", "review_commit",
        "review_evidence"
    };

    /// <summary>mind 工具集：review 白名单等复用（新心智工具）。</summary>
    internal static readonly string[] MindToolSeed =
    {
        "journal", "claim", "remember", "mind_status", "archive"
    };

    /// <summary>Session 配置文件 DTO。Type 声明会话类型键（与文件名一致）；DefaultComponents 对应 record 的 DefaultComponentIds。</summary>
    private sealed class SessionProfileFile
    {
        public string Type { get; set; } = "";
        public string DefaultCoreConfig { get; set; } = "";
        public string[] AllowedCores { get; set; } = Array.Empty<string>();
        public string[] PromptFiles { get; set; } = Array.Empty<string>();
        [JsonPropertyName("DefaultComponents")]
        public string[] DefaultComponentIds { get; set; } = Array.Empty<string>();
        public string PermissionGroup { get; set; } = "";
        public string PermissionMode { get; set; } = "";
        public string[] PermissionTools { get; set; } = Array.Empty<string>();
    }
}
