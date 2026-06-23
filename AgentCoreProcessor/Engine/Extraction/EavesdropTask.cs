// AgentCoreProcessor/Engine/Extraction/EavesdropTask.cs
using System;

namespace AgentCoreProcessor.Engine.Extraction;

/// <summary>
/// 监听任务定义。
/// </summary>
internal class EavesdropTask
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public int[] ChannelIds { get; set; } = Array.Empty<int>(); // 空=全频道
    public EavesdropRule[] Rules { get; set; } = Array.Empty<EavesdropRule>();
    public int NotificationTargetId { get; set; }
    public string Status { get; set; } = "active";
}

internal class EavesdropRule
{
    public string[] Keywords { get; set; } = Array.Empty<string>();
    public string Hint { get; set; } = "";
}
