// AgentLilara.PluginSDK/CreateSessionSpec.cs
using System.Collections.Generic;

namespace AgentLilara.PluginSDK;

/// <summary>
/// Session 创建规格。组件列表 + 等级 + 端口 + 超限参数 = 完整 Session 行为定义。
/// </summary>
public class CreateSessionSpec
{
    /// <summary>要激活的组件 ID 列表，如 ["basic-tools", "file-tools"]</summary>
    public string[] ComponentIds { get; set; } = System.Array.Empty<string>();

    /// <summary>工具等级上限，默认 L0</summary>
    public int MaxLevel { get; set; } = 0;

    /// <summary>自动 Full 占用归属频道（端口配置中的第一个 channelId）</summary>
    public bool AutoClaimChannel { get; set; } = false;

    /// <summary>输入端口：端口名 → PortSpec</summary>
    public Dictionary<string, PortSpec> InputPorts { get; set; } = new();

    /// <summary>输出端口：端口名 → 目标</summary>
    public Dictionary<string, string> OutputPorts { get; set; } = new();

    /// <summary>初始状态（ScheduledTask/TODO描述/历史消息），注入到 agent 首轮</summary>
    public object? InitialState { get; set; }

    public string? ParentSessionId { get; set; }
    public SessionLimits? OverrideLimits { get; set; }

    /// <summary>Session 使用的 Core 配置文件名（不含路径和扩展名），默认 "SessionCore"</summary>
    public string CoreConfigName { get; set; } = "SessionCore";
}
