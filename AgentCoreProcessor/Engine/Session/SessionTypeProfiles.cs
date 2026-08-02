// AgentCoreProcessor/Engine/Session/SessionTypeProfiles.cs
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 会话类型档案加载器。从 Storage/Session/{type}.json 读取（channel.json / task.json），
/// 缓存 + Invalidate。替代旧 PluginSDK 中硬编码的 SessionTypeProfiles。
/// 文件缺失或损坏时回退到内置默认（与种子一致），保证冷启动可用。
/// </summary>
internal static class SessionTypeProfiles
{
    private static string Dir => Path.Combine(PathConfig.StoragePath, "Session");

    private static readonly ConcurrentDictionary<SessionType, SessionTypeProfile> _cache = new();

    public static SessionTypeProfile Get(SessionType type)
    {
        if (_cache.TryGetValue(type, out var cached)) return cached;
        var profile = Load(type);
        _cache[type] = profile;
        return profile;
    }

    public static void Invalidate() => _cache.Clear();

    public static string KeyFor(SessionType type) => type switch
    {
        SessionType.Channel => "channel",
        SessionType.Task => "task",
        _ => type.ToString().ToLowerInvariant()
    };

    private static SessionTypeProfile Load(SessionType type)
    {
        var path = Path.Combine(Dir, $"{KeyFor(type)}.json");
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
                        DefaultComponentIds = file.DefaultComponentIds?.Length > 0
                            ? file.DefaultComponentIds : DefaultFor(type).DefaultComponentIds,
                        PermissionGroup = string.IsNullOrEmpty(file.PermissionGroup)
                            ? KeyFor(type) : file.PermissionGroup,
                        PermissionMode = string.IsNullOrEmpty(file.PermissionMode)
                            ? "whitelist" : file.PermissionMode,
                        PermissionTools = file.PermissionTools ?? Array.Empty<string>()
                    };
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "会话类型配置加载失败，使用默认",
                    new { type = type.ToString(), path, error = ex.Message });
            }
        }
        return DefaultFor(type);
    }

    /// <summary>内置默认：与 Storage/Session 种子一致，文件缺失时兜底。</summary>
    private static SessionTypeProfile DefaultFor(SessionType type) => type switch
    {
        SessionType.Channel => new SessionTypeProfile
        {
            Type = SessionType.Channel,
            DefaultCoreConfig = "ChannelCore",
            AllowedCoreConfigs = new[] { "ChannelCore" },
            DefaultComponentIds = new[]
            {
                "basic-tools", "working-tools", "file-tools", "file-ops",
                "memory-tools", "system-ops", "cross-loop"
            },
            PermissionGroup = "channel",
            PermissionMode = "blacklist",
            PermissionTools = new[] { "escalate" }.Concat(ReviewToolSeed).ToArray()
        },
        SessionType.Task => new SessionTypeProfile
        {
            Type = SessionType.Task,
            DefaultCoreConfig = "TaskCore",
            AllowedCoreConfigs = new[] { "TaskCore" },
            DefaultComponentIds = new[]
            {
                "basic-tools", "file-tools", "memory-tools", "cross-loop",
                "sub-agent-runtime"
            },
            PermissionGroup = "task",
            PermissionMode = "whitelist",
            PermissionTools = TaskToolSeed
        },
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>task 组白名单种子：Task profile 组件工具 + 核心 session 工具（不含全局工具）。</summary>
    internal static readonly string[] TaskToolSeed =
    {
        // basic-tools
        "speak", "thinking", "send_media", "send_file", "adapter_action",
        // file-tools
        "read_text", "write_text", "list_dir", "move_file", "delete_file", "copy_file", "update_text",
        // memory-tools
        "memory_store", "memory_get", "memory_search", "memory_list", "memory_stats",
        "memory_delete", "memory_link_create", "memory_link_delete", "memory_link_get", "memory_update",
        // cross-loop
        "send_message", "list_loops",
        // sub-agent-runtime
        "report_to_parent",
        // 核心 session 工具
        "end_session", "wait", "compact", "connect_channel", "listen_channel",
        "disconnect_channel", "get_image_text", "refine_image", "continue_loop"
    };

    /// <summary>review 工具集：channel 黑名单与 review 白名单共用（Working 会话不可用复盘工具）。</summary>
    internal static readonly string[] ReviewToolSeed =
    {
        "review_browse", "review_complete", "review_evaluate", "review_focus",
        "review_get_person", "review_list_beacons", "review_log", "review_remove_beacon",
        "review_save_progress", "review_search_messages", "review_thinking_notes",
        "review_update_person"
    };

    /// <summary>memory 工具集：review 白名单等复用。</summary>
    internal static readonly string[] MemoryToolSeed =
    {
        "memory_store", "memory_get", "memory_search", "memory_list", "memory_stats",
        "memory_delete", "memory_link_create", "memory_link_delete", "memory_link_get", "memory_update"
    };

    /// <summary>Session 配置文件 DTO。DefaultComponents 对应 record 的 DefaultComponentIds。</summary>
    private sealed class SessionProfileFile
    {
        public string DefaultCoreConfig { get; set; } = "";
        public string[] AllowedCores { get; set; } = Array.Empty<string>();
        [JsonPropertyName("DefaultComponents")]
        public string[] DefaultComponentIds { get; set; } = Array.Empty<string>();
        public string PermissionGroup { get; set; } = "";
        public string PermissionMode { get; set; } = "";
        public string[] PermissionTools { get; set; } = Array.Empty<string>();
    }
}
