// AgentCoreProcessor/Tool/PermissionService.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Tool;

/// <summary>
/// 工具权限组裁决。读取 Storage/Session/{group}.json 中内嵌的工具黑白名单
/// （channel/task 是会话类型文件，express/review 是纯权限文件），统一决定某工具对某权限组是否可见/可执行。
///
/// 语义：
///   - whitelist（白名单）：仅清单中的工具可用
///   - blacklist（黑名单）：清单中的工具被禁用
///   - 组文件不存在 → 放行（不限制）
/// 这是工具权限的单一裁决源，替代旧的 ToolMetaAttribute.EngineTypes 与 EngineTools.json。
/// </summary>
internal static class PermissionService
{
    private static string Dir => Path.Combine(PathConfig.StoragePath, "Session");

    private static readonly ConcurrentDictionary<string, PermissionGroupEntry?> _cache = new();

    /// <summary>判断工具对指定权限组是否允许。group 为空 → 放行。</summary>
    public static bool IsAllowed(string? group, string toolName)
    {
        if (string.IsNullOrEmpty(group)) return true;
        var entry = GetOrLoad(group);
        if (entry == null) return true; // 组未配置 → 放行
        return entry.IsAllowed(toolName);
    }

    /// <summary>取某组的工具清单（白名单=允许项；黑名单=禁用项）。供 WebUI 展示。</summary>
    public static IReadOnlyList<string> GetTools(string group)
    {
        var entry = GetOrLoad(group);
        return entry?.PermissionTools ?? new List<string>();
    }

    public static string GetMode(string group)
    {
        var entry = GetOrLoad(group);
        return entry?.PermissionMode ?? "whitelist";
    }

    /// <summary>取某组的默认 Core 配置名（express.json 的 DefaultCoreConfig；channel/task 亦支持）。</summary>
    public static string GetCoreConfig(string group)
    {
        var entry = GetOrLoad(group);
        return string.IsNullOrEmpty(entry?.DefaultCoreConfig) ? "" : entry.DefaultCoreConfig;
    }

    /// <summary>已知权限组（express/channel/task/review）。供 WebUI 展示。</summary>
    public static readonly string[] KnownGroups = { "express", "channel", "task", "review" };

    /// <summary>
    /// 保存某权限组的模式与工具清单。仅改写文件内 PermissionMode/PermissionTools 字段，
    /// 保留其余字段（channel/task 的 Type/Core/Components）。写后双缓存失效。
    /// </summary>
    public static void Save(string group, string mode, IEnumerable<string> tools)
    {
        var path = Path.Combine(Dir, $"{group}.json");
        var node = File.Exists(path)
            ? (JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject())
            : new JsonObject();

        node["PermissionMode"] = string.Equals(mode, "blacklist", StringComparison.OrdinalIgnoreCase)
            ? "blacklist" : "whitelist";
        var arr = new JsonArray();
        foreach (var t in tools.Distinct(StringComparer.OrdinalIgnoreCase))
            arr.Add(t);
        node["PermissionTools"] = arr;

        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Invalidate();
            Engine.Session.SessionTypeProfiles.Invalidate();
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "权限组配置保存失败", new { group, path, error = ex.Message });
        }
    }

    /// <summary>取某权限组的默认（恢复默认用）。channel/task 与 SessionTypeProfiles 种子一致。</summary>
    public static (string Mode, string[] Tools) GetDefault(string group) => group switch
    {
        "express" => ("whitelist", new[]
        {
            "speak", "thinking", "send_media", "send_file", "adapter_action",
            "escalate", "wait",
            "journal", "remember", "mind_status"
        }),
        "channel" => ("blacklist", new[] { "escalate" }.Concat(Engine.Session.SessionTypeProfiles.ReviewToolSeed).ToArray()),
        "task" => ("whitelist", Engine.Session.SessionTypeProfiles.TaskToolSeed),
        "review" => ("whitelist", Engine.Session.SessionTypeProfiles.ReviewToolSeed
            .Concat(Engine.Session.SessionTypeProfiles.MindToolSeed).ToArray()),
        _ => ("whitelist", Array.Empty<string>())
    };

    public static void Invalidate() => _cache.Clear();

    private static PermissionGroupEntry? GetOrLoad(string group)
    {
        if (_cache.TryGetValue(group, out var cached)) return cached;
        var entry = Load(group);
        _cache[group] = entry;
        return entry;
    }

    private static PermissionGroupEntry? Load(string group)
    {
        var path = Path.Combine(Dir, $"{group}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<PermissionGroupEntry>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return file;
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "权限组配置加载失败，该组不限制",
                new { group, path, error = ex.Message });
            return null;
        }
    }
}

/// <summary>单个权限组的配置（文件内 PermissionMode + PermissionTools + DefaultCoreConfig；会话类型文件中的其余字段被忽略）。</summary>
internal sealed class PermissionGroupEntry
{
    public string PermissionMode { get; set; } = "whitelist";

    public List<string> PermissionTools { get; set; } = new();

    /// <summary>该组加载的默认 Core 配置名（express.json 使用；channel/task 由 SessionTypeProfiles 读取）。</summary>
    public string DefaultCoreConfig { get; set; } = "";

    public bool IsWhitelist =>
        string.Equals(PermissionMode, "whitelist", StringComparison.OrdinalIgnoreCase);

    public bool IsAllowed(string toolName)
    {
        var inList = PermissionTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);
        return IsWhitelist ? inList : !inList;
    }
}
