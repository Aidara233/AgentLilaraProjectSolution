// AgentLilara.PluginSDK/SessionType.cs
namespace AgentLilara.PluginSDK;

/// <summary>
/// Session 类型配置档案。由宿主从 Storage/Session/{type}.json 配置库加载（见 AgentCoreProcessor/Engine/Session/SessionTypeProfiles.cs），
/// 本 record 仅承载数据。类型键 = 配置库文件名（"channel"/"task"/"review" 等），任意新类型只需新增一个 JSON 文件。
/// 每个会话类型内嵌自己的工具权限组（PermissionMode + PermissionTools）。
/// </summary>
public record SessionTypeProfile
{
    /// <summary>会话类型键，与 Storage/Session/{type}.json 文件名一致。</summary>
    public string Type { get; init; } = "";

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
