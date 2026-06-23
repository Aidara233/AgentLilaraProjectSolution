using System;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 控制信号总线。仅承载基础设施控制信号（EngineStart/Stop、ConfigChange、timer-tick、
    /// force-sleep、force-review、new-image、refine-image 等）。业务消息已迁移至
    /// MessageEngine / AdapterEngine。
    /// </summary>
    public class EventBus
    {
        private readonly object lockObj = new();

        public event Action<EngineEvent>? OnEvent;

        public void Publish(EngineEvent e)
        {
            e.TraceSignalId ??= SignalContext.Current?.SignalId;
            e.TraceParentSpanId ??= SignalContext.Current?.CurrentSpanId;
            lock (lockObj)
            {
                OnEvent?.Invoke(e);
            }
        }

        /// <summary>便捷方法：发布信号事件（工具回环/内部控制用）。</summary>
        public void PublishSignal(string signalName, object? payload = null)
        {
            Publish(new SignalEvent { SignalName = signalName, Payload = payload });
        }
    }
}
