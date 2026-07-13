using System;

namespace AgentCoreProcessor.Engine;

/// <summary>
/// 循环间单发消息。fire-and-forget，无状态机、无 response。
/// 回复就是对方再 SendMessage 一条新的。
/// </summary>
internal class LoopMessage
{
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public string InitiatorId { get; set; } = "";
    /// <summary>null 表示广播。</summary>
    public string? TargetId { get; set; }
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
}