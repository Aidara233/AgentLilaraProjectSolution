// AgentCoreProcessor/Engine/Context/ChannelContextState.cs
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>
/// 单个消费者（ExpressEngine 或 RuntimeSession）的频道上下文可变状态。
/// ChannelContextService 操作此对象，自身不持状态。
/// </summary>
internal class ChannelContextState
{
    public int ChannelId { get; set; }
    public string? ChannelName { get; set; }
    public string Mode { get; set; } = "express";   // "express" | "session"
    public string? FixedPrefix { get; set; }
    public string? ContextSummary { get; set; }
    public int LastConsumedMessageId { get; set; }
    public int StartInjectMaxId { get; set; }

    /// <summary>增量新消息最近一次渲染时的本地日期（只有日期部分，跨日判定用）。</summary>
    public DateTime? LastRenderedLocalDate { get; set; }

    public HashSet<string> RoundImageHashes { get; } = new();
    public HashSet<string> InjectedDescriptions { get; } = new();
    public HashSet<string> InjectedOcrTexts { get; } = new();
}
