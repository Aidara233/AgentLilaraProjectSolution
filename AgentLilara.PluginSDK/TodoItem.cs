// AgentLilara.PluginSDK/TodoItem.cs
using System;

namespace AgentLilara.PluginSDK;

/// <summary>
/// 无确切时间的待办事项，靠 Session 主动想起。
/// </summary>
public class TodoItem
{
    public string TodoId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Description { get; set; } = "";
    public int? RelatedPersonId { get; set; }
    public int? RelatedChannelId { get; set; }
    public decimal MinBalance { get; set; } = 0;
    public DateTime NotBefore { get; set; } = DateTime.UtcNow;
    public DateTime? NotAfter { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public bool RequiresInteraction { get; set; }
}
