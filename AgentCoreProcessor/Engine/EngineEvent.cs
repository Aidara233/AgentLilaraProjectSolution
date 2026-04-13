using System;
using AgentCoreProcessor.Adapter;

namespace AgentCoreProcessor.Engine
{
    public enum EngineEventType
    {
        Message,
        Timer,
        Idle,
        Signal,
        System,
    }

    public abstract class EngineEvent
    {
        public abstract EngineEventType Type { get; }
        public DateTime Time { get; set; } = DateTime.Now;
    }

    /// <summary>用户消息事件，包装 IncomingMessage。</summary>
    public class MessageEvent : EngineEvent
    {
        public override EngineEventType Type => EngineEventType.Message;
        public required IncomingMessage Message { get; set; }
    }

    /// <summary>定时触发事件（预留，做梦调度用）。</summary>
    public class TimerEvent : EngineEvent
    {
        public override EngineEventType Type => EngineEventType.Timer;
        public string TimerName { get; set; } = "";
    }

    /// <summary>空闲触发事件（预留，走神/小睡用）。</summary>
    public class IdleEvent : EngineEvent
    {
        public override EngineEventType Type => EngineEventType.Idle;
        public TimeSpan IdleDuration { get; set; }
    }

    /// <summary>信号事件（预留，工具回环用，如睡眠许可）。</summary>
    public class SignalEvent : EngineEvent
    {
        public override EngineEventType Type => EngineEventType.Signal;
        public string SignalName { get; set; } = "";
        public object? Payload { get; set; }
    }

    /// <summary>系统事件（预留，启动/关闭等）。</summary>
    public class SystemEvent : EngineEvent
    {
        public override EngineEventType Type => EngineEventType.System;
        public SystemAction Action { get; set; }
    }

    public enum SystemAction
    {
        Started,
        Stopping,
    }
}
