// AgentCoreProcessor/Engine/Session/SessionEngine.Pool.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// <see cref="SessionEngine"/> 的部件：会话池（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>含子池创建/查找、池快照与空池回收；池与成员索引的字段仍留在主文件（跨部件共用状态）。</para>
/// </summary>
internal partial class SessionEngine
{
    /// <summary>
    /// 为子 agent 创建子池：挂到父会话所在的池（直接父池），子池 ActiveSession = 子 agent。
    /// 子池 ChannelId=0（无频道、不持 claim），独享自己的池。
    /// </summary>
    internal void CreateSubPool(string childId, string parentSessionId)
    {
        lock (_poolLock)
        {
            if (_poolByMember.ContainsKey(childId)) return;
            if (!_poolByMember.TryGetValue(parentSessionId, out var parentPool)) return;
            var sub = new SessionPool(0) { Parent = parentPool };
            sub.Claim(childId);
            parentPool.ChildPools.Add(sub);
            _poolByMember[childId] = sub;
        }
    }

    /// <summary>按旧成员定位接管顶层或子池，活跃身份与新成员索引在同一锁内发布。</summary>
    internal bool TakeOverSessionPool(string oldSessionId, string newSessionId)
    {
        lock (_poolLock)
        {
            if (!_poolByMember.TryGetValue(oldSessionId, out var pool)) return false;
            if (_poolByMember.TryGetValue(newSessionId, out var existing) && !ReferenceEquals(existing, pool))
                return false;
            pool.TakeOver(newSessionId);
            _poolByMember[newSessionId] = pool;
            return true;
        }
    }

    /// <summary>按成员查找所在池。无池返回 null。</summary>
    private SessionPool? FindPoolBySession(string sessionId)
    {
        lock (_poolLock)
            return _poolByMember.TryGetValue(sessionId, out var pool) ? pool : null;
    }

    /// <summary>会话所在的资源池（WebUI 详情/池树展示用）。无池返回 null。</summary>
    public SessionPool? GetPoolBySession(string sessionId) => FindPoolBySession(sessionId);

    /// <summary>全部资源池快照（WebUI 池树展示用；复制后调用方无需持锁）。</summary>
    public List<SessionPool> GetPoolsSnapshot()
    {
        lock (_poolLock)
        {
            var seen = new HashSet<SessionPool>();
            var result = new List<SessionPool>();
            void Add(SessionPool pool)
            {
                if (!seen.Add(pool)) return;
                result.Add(pool);
                foreach (var child in pool.ChildPools)
                    Add(child);
            }

            foreach (var pool in _pools.Values)
                Add(pool);
            return result;
        }
    }

    /// <summary>空池回收：顶层池从 _pools 移除 / 子池从父移除，并清空其成员索引（成员已全部死亡）。</summary>
    private void RemoveEmptyPool(SessionPool pool)
    {
        if (!pool.IsEmpty) return;
        if (pool.IsRoot)
        {
            if (_pools.TryGetValue(pool.ChannelId, out var current) && ReferenceEquals(current, pool))
            {
                _pools.TryRemove(pool.ChannelId, out _);
                if (pool.Occupancy is { } occupancy) _occupancies.Remove(pool.ChannelId, occupancy);
            }
        }
        else
            pool.Parent?.ChildPools.Remove(pool);
        RemovePoolMemberIndexes(pool);
    }

    /// <summary>调用者持有 _poolLock；历史成员可能已经归属另一池。</summary>
    private void RemovePoolMemberIndexes(SessionPool pool)
    {
        foreach (var member in pool.Members)
            if (_poolByMember.TryGetValue(member, out var indexed) && ReferenceEquals(indexed, pool))
                _poolByMember.TryRemove(member, out _);
    }
}
