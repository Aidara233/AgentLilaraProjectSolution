using System.Collections.Generic;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine
{
    public abstract record ChannelSignal;

    /// <summary>新消息到达（用户发言、系统推送等）。</summary>
    public record NewMessageSignal(IncomingMessage Message, SessionContext Session) : ChannelSignal;

    /// <summary>EventBus 事件到达（委托完成、系统通知等）。</summary>
    public record BusEventSignal(EngineEvent Event) : ChannelSignal;

    /// <summary>压缩完成。包含新摘要 + 保留的对话历史。</summary>
    public record CompressionSignal(string Summary, List<Message> RetainedHistory) : ChannelSignal;

    /// <summary>模式切换（Express ↔ Working）。</summary>
    public record ModeSwitchSignal(string NewMode, string? Reason) : ChannelSignal;
}
