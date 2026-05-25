namespace AgentCoreProcessor.Engine;

/// <summary>
/// 信号分类。用于过滤器决定哪些信号能唤醒引擎、哪些对模型可见。
/// </summary>
public enum SignalCategory
{
    /// <summary>用户在频道中发送的真实消息。</summary>
    ChannelMessage,

    /// <summary>委托状态变更通知（接受/拒绝/完成/失败/进度）。</summary>
    Delegation,

    /// <summary>系统事件（定时任务到期、收到新委托请求等）。</summary>
    SystemEvent,

    /// <summary>关注列表命中。</summary>
    WatchSignal,

    /// <summary>内部控制信号（压缩完成、模式切换等）。始终通过，不可过滤。</summary>
    Internal
}
