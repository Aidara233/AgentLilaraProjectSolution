// AgentLilara.PluginSDK/CreateSessionSpec.cs
using System.Collections.Generic;

namespace AgentLilara.PluginSDK;

/// <summary>
/// Session 创建规格。组件列表 + 端口 + 初始状态 = 完整 Session 行为定义。
/// </summary>
public class CreateSessionSpec
{
    /// <summary>Session 类型，决定默认组件集、Core 配置、LoopApplicability key</summary>
    public SessionType Type { get; set; } = SessionType.Channel;

    /// <summary>预计算的组件列表（compact 迁移用）。非 null 则跳过 Profile 计算</summary>
    public string[]? ResolvedComponentIds { get; set; }

    /// <summary>在 Profile 默认基础上追加的组件</summary>
    public string[]? AdditionalComponents { get; set; }

    /// <summary>从 Profile 默认中排除的组件</summary>
    public string[]? ExcludedComponents { get; set; }

    /// <summary>归属频道 ID（任何类型可指定，escalate 路径可省略，由 InitActions.connect_channel 接力）</summary>
    public int? ChannelId { get; set; }

    /// <summary>自动 Full 占用归属频道（端口配置中的第一个 channelId）</summary>
    public bool AutoClaimChannel { get; set; } = false;

    /// <summary>输入端口：端口名 → PortSpec</summary>
    public Dictionary<string, PortSpec> InputPorts { get; set; } = new();

    /// <summary>输出端口：端口名 → 目标</summary>
    public Dictionary<string, string> OutputPorts { get; set; } = new();

    /// <summary>初始状态（ScheduledTask/TODO描述/历史消息），注入到 agent 首轮</summary>
    public object? InitialState { get; set; }

    /// <summary>
    /// 初始化工具调用列表。session 启动时（BuildStart 前）按顺序执行，
    /// 真实调用工具本体并应用副作用（如 connect_channel 连接频道）。
    /// 用于消解"带端口创建 vs 空端口创建"的特例：channel session 的初始化 =
    /// 一条 connect_channel 调用，任务/监听 session 则空起步、运行期自行连接。
    /// 执行结果以"初始化工具调用"系统消息汇总注入首轮上下文。
    /// </summary>
    public List<ToolCall> InitActions { get; set; } = new();

    public string? ParentSessionId { get; set; }
}
