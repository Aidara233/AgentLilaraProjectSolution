using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 循环内事件总线。生命周期 = ChannelEngine 实例。
    /// 与全局 EventBus 独立，用于内务模块之间的通信。
    /// </summary>
    public interface ILoopBus
    {
        void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class;
        void Publish<TEvent>(TEvent e) where TEvent : class;
    }

    internal class LoopBus : ILoopBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();

        public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            var type = typeof(TEvent);
            if (!_handlers.TryGetValue(type, out var list))
            {
                list = new List<Delegate>();
                _handlers[type] = list;
            }
            list.Add(handler);
        }

        public void Publish<TEvent>(TEvent e) where TEvent : class
        {
            var type = typeof(TEvent);
            if (!_handlers.TryGetValue(type, out var list)) return;
            foreach (var handler in list)
            {
                try
                {
                    ((Action<TEvent>)handler)(e);
                }
                catch (Exception)
                {
                }
            }
        }
    }
}
