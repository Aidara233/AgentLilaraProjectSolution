// AgentCoreProcessor/Engine/Session/SessionResourceRegistry.cs
using System.Collections.Concurrent;
using System.Collections.Generic;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// SessionEngine 内部持有的 session-scoped 子资源注册表。
/// 负责在 compact / 释放 / 死亡时回调所有 ISessionResource。
/// </summary>
internal class SessionResourceRegistry
{
    private readonly ConcurrentDictionary<ISessionResource, byte> _resources = new();

    public void Register(ISessionResource r) => _resources.TryAdd(r, 0);
    public void Unregister(ISessionResource r) => _resources.TryRemove(r, out _);

    public async System.Threading.Tasks.Task NotifyCompactingAsync(string oldSessionId, string newSessionId)
    {
        foreach (var r in _resources.Keys)
        {
            try { await r.OnSessionCompactingAsync(oldSessionId, newSessionId); }
            catch (System.Exception ex) { Signal.Warn(LogGroup.Engine, "ISessionResource OnSessionCompacting 异常", new { resource = r.GetType().Name, error = ex.Message }); }
        }
    }

    public async System.Threading.Tasks.Task NotifyFreedAsync(string sessionId)
    {
        foreach (var r in _resources.Keys)
        {
            try { await r.OnSessionFreedAsync(sessionId); }
            catch (System.Exception ex) { Signal.Warn(LogGroup.Engine, "ISessionResource OnSessionFreed 异常", new { resource = r.GetType().Name, error = ex.Message }); }
        }
    }

    public bool AnyHasActiveResources(string sessionId)
    {
        foreach (var r in _resources.Keys)
        {
            try { if (r.HasActiveResources(sessionId)) return true; }
            catch { /* best effort */ }
        }
        return false;
    }
}
