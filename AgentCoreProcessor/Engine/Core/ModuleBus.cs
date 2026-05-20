using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine
{
    /// <summary>每引擎独立的模块间通信总线。替代 ILoopBus + ComponentEventBus。</summary>
    public class ModuleBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();
        private readonly object _lock = new();

        public IDisposable Subscribe<T>(Action<T> handler) where T : class
        {
            var type = typeof(T);
            lock (_lock)
            {
                if (!_handlers.TryGetValue(type, out var list))
                {
                    list = new List<Delegate>();
                    _handlers[type] = list;
                }
                list.Add(handler);
            }
            return new Unsubscriber<T>(this, handler);
        }

        public void Publish<T>(T message) where T : class
        {
            List<Delegate>? snapshot;
            lock (_lock)
            {
                if (_handlers.TryGetValue(typeof(T), out var list))
                    snapshot = new List<Delegate>(list);
                else
                    return;
            }
            foreach (var h in snapshot)
                ((Action<T>)h)(message);
        }

        private class Unsubscriber<T> : IDisposable where T : class
        {
            private readonly ModuleBus _bus;
            private readonly Action<T> _handler;
            public Unsubscriber(ModuleBus bus, Action<T> handler) { _bus = bus; _handler = handler; }
            public void Dispose()
            {
                lock (_bus._lock)
                {
                    if (_bus._handlers.TryGetValue(typeof(T), out var list))
                        list.Remove(_handler);
                }
            }
        }
    }
}
