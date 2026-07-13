// AgentLilara.PluginSDK/SessionType.cs
using System;
using System.Collections.Generic;

namespace AgentLilara.PluginSDK;

/// <summary>
/// Session 类型枚举。决定默认组件集、Core 配置、LoopApplicability key。
/// </summary>
public enum SessionType { Channel, Task }

/// <summary>
/// Session 类型配置档案。预定义每种 SessionType 的默认行为。
/// </summary>
public record SessionTypeProfile
{
    public SessionType Type { get; init; }
    public string DefaultCoreConfig { get; init; } = "";
    public string[] DefaultComponentIds { get; init; } = Array.Empty<string>();
    public string LoopApplicabilityKey { get; init; } = "";
}

public static class SessionTypeProfiles
{
    public static SessionTypeProfile Get(SessionType t) => t switch
    {
        SessionType.Channel => Channel,
        SessionType.Task => Task,
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };

    public static readonly SessionTypeProfile Channel = new()
    {
        Type = SessionType.Channel,
        DefaultCoreConfig = "ChannelCore",
        DefaultComponentIds = new[]
        {
            "basic-tools", "working-tools", "file-tools", "file-ops",
            "memory-tools", "system-tools", "cross-loop"
        },
        LoopApplicabilityKey = "channel"
    };

    public static readonly SessionTypeProfile Task = new()
    {
        Type = SessionType.Task,
        DefaultCoreConfig = "TaskCore",
        DefaultComponentIds = new[]
        {
            "basic-tools", "file-tools", "memory-tools", "cross-loop",
            "sub-agent-runtime"
        },
        LoopApplicabilityKey = "task"
    };
}
