// AgentCoreProcessor/Engine/Session/SessionEngine.Subagent.cs
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
/// <see cref="SessionEngine"/> 的部件：子会话与池树关系（partial 文件，仅物理拆分，类型仍是一个）。
/// <para>含活跃子链查询、compact 后的活跃 session/父会话解析、子会话结束清理与退出时全量收尾。</para>
/// </summary>
internal partial class SessionEngine
{
    // ── 子链查询（资源池树）──

    /// <summary>某会话的活跃子 agent 列表。走池树：所在池的 ChildPools 的活跃 session。</summary>
    public IReadOnlyCollection<string>? GetActiveChildren(string sessionId)
    {
        lock (_poolLock)
        {
            if (!_poolByMember.TryGetValue(sessionId, out var pool)) return null;
            var children = pool.ChildPools
                .Where(cp => !string.IsNullOrEmpty(cp.ActiveSessionId))
                .Select(cp => cp.ActiveSessionId)
                .ToList();
            return children.Count > 0 ? children : null;
        }
    }

    /// <summary>
    /// 解析会话引用到当前活跃 session ID（走资源池树：引用 → 所在池 → 池活跃 session）。
    /// 输入为裸 sessionId（LoopId 前缀概念已移除）。查不到返回 null。
    /// </summary>
    public string? ResolveActiveSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        if (_poolByMember.TryGetValue(sessionId, out var pool) && !string.IsNullOrEmpty(pool.ActiveSessionId))
            return pool.ActiveSessionId;
        return null;
    }

    /// <summary>子 agent 兼容入口：返回 childId 的直接父 session ID。沿池树：子池 → 父池 → 父池活跃 session。</summary>
    public string? ResolveEffectiveParent(string childId)
    {
        if (_poolByMember.TryGetValue(childId, out var pool) && pool.Parent != null)
            return pool.Parent.ActiveSessionId;
        return null;
    }

    public void OnChildCompleted(string childId)
    {
        lock (_poolLock)
        {
            if (!_poolByMember.TryGetValue(childId, out var sub)) return;
            var parentPool = sub.Parent;
            if (parentPool != null)
            {
                parentPool.ChildPools.Remove(sub);
                var parentId = parentPool.ActiveSessionId;
                if (!string.IsNullOrEmpty(parentId))
                {
                    var parent = GetSession(parentId) as RuntimeSession;
                    parent?.RemoveChildFromState(childId);
                }
            }
            // 清理子池成员索引（子 agent 已结束）
            RemovePoolMemberIndexes(sub);
            if (parentPool is not null
                && (parentPool.IsRoot || string.IsNullOrEmpty(parentPool.ActiveSessionId)))
                RemoveEmptyPool(parentPool);
        }
    }

    /// <summary>完成所有活跃会话并落盘（--test 退出清理用）：标记 Completed，释放频道占用，避免残留 Running 阻塞下次验证。单会话失败不阻塞。</summary>
    public async Task CompleteAllSessionsAsync(string reason)
    {
        foreach (var s in _sessions.Values.ToList())
        {
            try
            {
                s.MarkCompletedForCleanup();
                await s.PersistStateAsync();
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "会话退出清理失败",
                    new { sessionId = s.SessionId, reason, error = ex.Message });
            }
        }
    }
}
