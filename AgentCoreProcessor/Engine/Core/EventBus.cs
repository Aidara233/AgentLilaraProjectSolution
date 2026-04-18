using System;
using AgentCoreProcessor.Adapter;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 轻量事件总线。所有触发源（适配器、定时器、工具回环等）通过此总线发布事件，
    /// MasterEngine 订阅并按事件类型路由。
    /// </summary>
    public class EventBus
    {
        private readonly object lockObj = new();

        public event Action<EngineEvent>? OnEvent;

        public void Publish(EngineEvent e)
        {
            FrameworkLogger.Log("EventBus", $"事件发布: type={e.Type}");
            lock (lockObj)
            {
                OnEvent?.Invoke(e);
            }
        }

        /// <summary>便捷方法：发布消息事件。</summary>
        public void PublishMessage(IncomingMessage msg)
        {
            Publish(new MessageEvent { Message = msg, Time = msg.Time });
        }

        /// <summary>便捷方法：发布信号事件（工具回环用）。</summary>
        public void PublishSignal(string signalName, object? payload = null)
        {
            Publish(new SignalEvent { SignalName = signalName, Payload = payload });
        }
    }
}
