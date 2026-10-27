// AgentCoreProcessor/Engine/Session/SessionEngine.ChannelClaim.cs
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
/// <see cref="SessionEngine"/> 的部件：频道占用与端口订阅（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>含 claim/listen 的建立与释放、占用查询、会话快照与端口订阅退订。锁字段 <c>_poolLock</c> 留在主文件（跨部件共用）。</para>
/// </summary>
internal partial class SessionEngine
{
    // ── 频道占用（占用状态在资源池里）──

    public bool HasActiveWorking(int channelId)
    {
        lock (_poolLock)
            return _pools.TryGetValue(channelId, out var pool) && pool.HasFullClaim;
    }

    /// <summary>供 RuntimeSession 连接动作查询频道名称。</summary>
    public Task<Database.Channel?> GetChannelByIdAsync(int channelId)
        => _ctx.Session.GetChannelByIdAsync(channelId);

    public async Task<string?> GetChannelNameAsync(int channelId) =>
        (await _ctx.Session.GetChannelByIdAsync(channelId))?.Name;

    /// <summary>供 RuntimeSession 连接动作订阅频道消息子流。</summary>
    public IInputPort SubscribeChannel(int channelId, string subStream, int weight)
    {
        var me = _ctx.GetActiveEnginesSnapshot().OfType<Messaging.MessageEngine>().FirstOrDefault();
        return me!.Subscribe(channelId, subStream, weight);
    }

    /// <summary>供 RuntimeSession 断开动作退订频道端口。</summary>
    public void UnsubscribeChannel(IInputPort port)
    {
        var me = _ctx.GetActiveEnginesSnapshot().OfType<Messaging.MessageEngine>().FirstOrDefault();
        me?.Unsubscribe(port);
    }

    public bool ClaimChannel(string sessionId, int channelId, ClaimLevel level)
    {
        lock (_poolLock)
        {
            if (!_pools.TryGetValue(channelId, out var pool))
            {
                var occupancy = channelId > 0 ? _occupancies.GetOrCreate(channelId) : null;
                pool = new SessionPool(channelId, occupancy);
                _pools[channelId] = pool;
            }
            if (level == ClaimLevel.Full)
            {
                if (!pool.Claim(sessionId)) return false;
                _poolByMember[sessionId] = pool;
            }
            else
            {
                pool.Listen(sessionId);
                _poolByMember[sessionId] = pool;
            }
            return true;
        }
    }

    public void ReleaseChannel(string sessionId, int channelId)
    {
        lock (_poolLock)
        {
            if (!_pools.TryGetValue(channelId, out var pool)) return;
            pool.Release(sessionId);
            pool.Unlisten(sessionId);
            RemoveEmptyPool(pool);
        }
    }

    public string? GetFullClaimer(int channelId)
    {
        lock (_poolLock)
            return _pools.TryGetValue(channelId, out var pool) && pool.HasFullClaim
                ? pool.ActiveSessionId : null;
    }

    public bool IsChannelClaimed(int channelId)
    {
        lock (_poolLock)
            return _pools.TryGetValue(channelId, out var pool) && pool.HasFullClaim;
    }

    /// <summary>某频道的活跃会话快照（express/working）。无占用返回 null。</summary>
    public ChannelSessionView? GetChannelSessionSnapshot(int channelId)
    {
        lock (_poolLock)
        {
            if (!_pools.TryGetValue(channelId, out var pool) || !pool.HasFullClaim) return null;
            if (!_sessions.TryGetValue(pool.ActiveSessionId, out var s)) return null;
            return new ChannelSessionView(
                channelId, s.SessionId, s.Type, s.IsAlive,
                s.LastActivityInternal, (s as RuntimeSession)?.RoundCountInternal ?? 0);
        }
    }

    /// <summary>所有频道的活跃会话快照。</summary>
    public List<ChannelSessionView> GetChannelSessionSnapshots()
    {
        lock (_poolLock)
        {
            var list = new List<ChannelSessionView>();
            foreach (var pool in _pools.Values)
            {
                if (!pool.HasFullClaim) continue;
                if (!_sessions.TryGetValue(pool.ActiveSessionId, out var s)) continue;
                list.Add(new ChannelSessionView(
                    pool.ChannelId, s.SessionId, s.Type, s.IsAlive,
                    s.LastActivityInternal, (s as RuntimeSession)?.RoundCountInternal ?? 0));
            }
            return list;
        }
    }

    internal void ReleaseSessionClaims(string sessionId)
    {
        lock (_poolLock)
        {
            if (!_poolByMember.TryGetValue(sessionId, out var pool)) return;
            if (pool.ActiveSessionId == sessionId)
            {
                pool.Release(sessionId);
                RemoveEmptyPool(pool);
            }
        }
    }
}
