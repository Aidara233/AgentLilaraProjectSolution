// AgentCoreProcessor/Component/ComponentEventBus.cs
using System.Collections.Concurrent;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Component;

internal class ComponentEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _globalHandlers = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Type, List<Delegate>>> _localHandlers = new();

    public void SubscribeGlobal<TEvent>(Func<TEvent, Task> handler) where TEvent : class
    {
        var list = _globalHandlers.GetOrAdd(typeof(TEvent), _ => new List<Delegate>());
        lock (list) { list.Add(handler); }
    }

    public void UnsubscribeGlobal<TEvent>(Func<TEvent, Task> handler) where TEvent : class
    {
        if (_globalHandlers.TryGetValue(typeof(TEvent), out var list))
            lock (list) { list.Remove(handler); }
    }

    public void SubscribeLocal<TEvent>(string loopId, Func<TEvent, Task> handler) where TEvent : class
    {
        var loopHandlers = _localHandlers.GetOrAdd(loopId, _ => new());
        var list = loopHandlers.GetOrAdd(typeof(TEvent), _ => new List<Delegate>());
        lock (list) { list.Add(handler); }
    }

    public void UnsubscribeLocal<TEvent>(string loopId, Func<TEvent, Task> handler) where TEvent : class
    {
        if (_localHandlers.TryGetValue(loopId, out var loopHandlers))
            if (loopHandlers.TryGetValue(typeof(TEvent), out var list))
                lock (list) { list.Remove(handler); }
    }

    public async Task PublishGlobalAsync<TEvent>(TEvent e) where TEvent : class
    {
        // 发送到所有 global 订阅者
        if (_globalHandlers.TryGetValue(typeof(TEvent), out var globalList))
        {
            List<Delegate> snapshot;
            lock (globalList) { snapshot = globalList.ToList(); }
            foreach (var handler in snapshot)
            {
                try { await ((Func<TEvent, Task>)handler)(e); }
                catch (Exception ex) { LogError(ex, typeof(TEvent).Name, "global"); }
            }
        }

        // 发送到所有 loop 的 local 订阅者（global 事件对所有人可见）
        foreach (var (loopId, loopHandlers) in _localHandlers)
        {
            if (loopHandlers.TryGetValue(typeof(TEvent), out var localList))
            {
                List<Delegate> snapshot;
                lock (localList) { snapshot = localList.ToList(); }
                foreach (var handler in snapshot)
                {
                    try { await ((Func<TEvent, Task>)handler)(e); }
                    catch (Exception ex) { LogError(ex, typeof(TEvent).Name, loopId); }
                }
            }
        }
    }

    public async Task PublishLocalAsync<TEvent>(string loopId, TEvent e) where TEvent : class
    {
        if (!_localHandlers.TryGetValue(loopId, out var loopHandlers)) return;
        if (!loopHandlers.TryGetValue(typeof(TEvent), out var list)) return;

        List<Delegate> snapshot;
        lock (list) { snapshot = list.ToList(); }
        foreach (var handler in snapshot)
        {
            try { await ((Func<TEvent, Task>)handler)(e); }
            catch (Exception ex) { LogError(ex, typeof(TEvent).Name, loopId); }
        }
    }

    public void RemoveLoop(string loopId) => _localHandlers.TryRemove(loopId, out _);

    private static void LogError(Exception ex, string eventType, string scope)
    {
    }
}
