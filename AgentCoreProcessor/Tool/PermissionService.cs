// AgentCoreProcessor/Tool/PermissionService.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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

/// <summary>单个权限组的配置（文件内 PermissionMode + PermissionTools 字段；会话类型文件中的其余字段被忽略）。</summary>
internal sealed class PermissionGroupEntry
{
    public string PermissionMode { get; set; } = "whitelist";

    public List<string> PermissionTools { get; set; } = new();

    public bool IsWhitelist =>
        string.Equals(PermissionMode, "whitelist", StringComparison.OrdinalIgnoreCase);

    public bool IsAllowed(string toolName)
    {
        var inList = PermissionTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);
        return IsWhitelist ? inList : !inList;
    }
}
