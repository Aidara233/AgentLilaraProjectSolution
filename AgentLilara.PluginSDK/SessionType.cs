// AgentLilara.PluginSDK/SessionType.cs
using System;

namespace AgentLilara.PluginSDK;

/// <summary>
/// Session 类型枚举。决定默认组件集、Core 配置、权限组。
/// </summary>
public enum SessionType { Channel, Task }

/// <summary>
/// Session 类型配置档案。由宿主从 Storage/Session/{type}.json 加载（见 AgentCoreProcessor/Engine/Session/SessionTypeProfiles.cs），
/// 本 record 仅承载数据。每个会话类型内嵌自己的工具权限组（PermissionMode + PermissionTools）。
/// </summary>
public record SessionTypeProfile
{
    public SessionType Type { get; init; }
    public string DefaultCoreConfig { get; init; } = "";
    public string[] DefaultComponentIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 该会话类型可用的 Core 配置名白名单（实例在列表内选择）。
    /// 空数组 → 仅 DefaultCoreConfig 单核（当前行为）。运行时切换见 RuntimeSession.SwitchCore。
    /// </summary>
    public string[] AllowedCoreConfigs { get; init; } = Array.Empty<string>();

    /// <summary>权限组 key（"channel"/"task"），对应 Storage/Session/{key}.json 的工具黑白名单。</summary>
    public string PermissionGroup { get; init; } = "";

    /// <summary>权限模式："whitelist"（白名单，仅列出的工具可用）/ "blacklist"（黑名单，列出被禁用）。</summary>
    public string PermissionMode { get; init; } = "whitelist";

    /// <summary>该权限组的工具清单（白名单=允许项 / 黑名单=禁用项）。</summary>
    public string[] PermissionTools { get; init; } = Array.Empty<string>();
}
