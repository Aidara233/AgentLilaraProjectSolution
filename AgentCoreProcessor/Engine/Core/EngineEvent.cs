using System;

namespace AgentCoreProcessor.Engine
{
    public enum EngineEventType
    {
        Signal,
        System,
    }

    public abstract class EngineEvent
    {
        public abstract EngineEventType Type { get; }
        public DateTime Time { get; set; } = DateTime.Now;

        /// <summary>事件是否已被消费（如命令拦截），后续 SpawnCheck 应跳过。</summary>
        public bool Consumed { get; set; }

        /// <summary>信号追踪：发布时自动捕获的上游信号 ID。</summary>
        public string? TraceSignalId { get; set; }
        /// <summary>信号追踪：发布时自动捕获的上游 span ID。</summary>
        public string? TraceParentSpanId { get; set; }
    }

    /// <summary>信号事件（内部控制信号：force-sleep、force-review、timer-tick、new-image、refine-image 等）。</summary>
    public class SignalEvent : EngineEvent
    {
        public override EngineEventType Type => EngineEventType.Signal;
        public string SignalName { get; set; } = "";
        public object? Payload { get; set; }
    }

    /// <summary>系统事件（启动/停止/配置变更等基础设施控制信号）。</summary>
    public class SystemEvent : EngineEvent
    {
        public override EngineEventType Type => EngineEventType.System;
        public SystemAction Action { get; set; }
    }

    public enum SystemAction
    {
        Started,
        Stopping,
        ConfigChange,
    }
}
