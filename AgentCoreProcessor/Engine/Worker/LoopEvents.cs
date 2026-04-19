using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Engine
{
    // ---- 循环发布的事件 ----

    /// <summary>单个工具执行完毕后发布。</summary>
    internal record ToolExecutedEvent(ToolCall Call, ToolResult Result, ITool? ToolDef);

    /// <summary>每轮结束时发布。</summary>
    internal record RoundEndingEvent(int Round, int MaxRounds, int SilentRounds, int MaxSilentRounds);

    // ---- 模块发布的事件 ----

    /// <summary>模块请求向用户发送消息。</summary>
    internal record SpeakRequestedEvent(string Content);

    /// <summary>模块请求存储记忆。</summary>
    internal record MemoryStoreEvent(string Content);

    /// <summary>模块请求发送信号（睡眠许可等）。</summary>
    internal record SignalEmitEvent(string Name, string? Payload);
}
