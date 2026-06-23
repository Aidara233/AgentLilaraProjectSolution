// AgentLilara.PluginSDK/SessionLimits.cs
using System;

namespace AgentLilara.PluginSDK;

/// <summary>
/// Session 生命周期限制。MaxMessages 不设硬限制，轮次由经济模型约束。
/// </summary>
public class SessionLimits
{
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
}
