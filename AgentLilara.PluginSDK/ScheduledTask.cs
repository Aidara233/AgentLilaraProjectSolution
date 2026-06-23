// AgentLilara.PluginSDK/ScheduledTask.cs
using System;

namespace AgentLilara.PluginSDK;

public enum ScheduleType { Once, Recurring }

/// <summary>
/// 有确切时间的定时任务，合并 Once/Recurring。
/// </summary>
public class ScheduledTask
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Description { get; set; } = "";
    public ScheduleType Type { get; set; } = ScheduleType.Once;
    public DateTime ExecuteAt { get; set; }
    public string? CronExpression { get; set; }
    public int? SourceChannelId { get; set; }
    public int? SourcePersonId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
