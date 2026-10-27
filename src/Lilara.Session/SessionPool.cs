// AgentCoreProcessor/Engine/Session/SessionPool.cs
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 资源池。会话体系的统一抽象，一棵树：
/// - 顶层池 = 一个频道，持有频道占用（Full claim），活跃 session = 占用频道的 express/working。
/// - 子池 = 一个子 agent，挂在父会话所在池下，活跃 session = 子 agent 自己（独享）。
///
/// 一个池只能有一个活跃 session：express escalate 到 working / compact 时**同池换活跃 session**，
/// 占用状态原地不动（不释放、不重取）。
///
/// 关键语义：**claim 只属于顶层池**（ChannelId > 0）。子池无频道（ChannelId = 0），只有活跃 session，
/// 永不误判占用。子 agent "属于父池"（父池.ChildPools 有它）+ "是自己池的根"（子池.ActiveSession = 它）
/// 是两个维度，互不冲突。
/// </summary>
internal sealed class SessionPool
{
    public SessionPool(int channelId)
    {
        ChannelId = channelId;
        if (channelId > 0) _occupancy = new ChannelOccupancyState();
    }

    /// <summary>顶层池对应的频道；子池为 0（无频道，不持有 claim）。</summary>
    public int ChannelId { get; }

    /// <summary>当前活跃 session（顶层池 = 占用频道的 express/working；子池 = 子 agent 自己）。</summary>
    private readonly ChannelOccupancyState? _occupancy;
    private string _childActiveSessionId = "";
    private readonly List<string> _childListeners = new();
    public string ActiveSessionId => _occupancy?.ActiveSessionId ?? _childActiveSessionId;

    /// <summary>曾活跃于此池的所有 session（含被 compact 换掉的旧 id），用于寻址历史引用。</summary>
    public HashSet<string> Members { get; } = new();

    /// <summary>父池：子池 → 直接父会话所在的池；顶层池 → null。</summary>
    public SessionPool? Parent { get; set; }

    /// <summary>直接子池（子 agent 的池）。</summary>
    public List<SessionPool> ChildPools { get; } = new();

    /// <summary>是否顶层池（有频道）。</summary>
    public bool IsRoot => ChannelId > 0;

    /// <summary>是否持有 Full claim：仅顶层池且活跃 session 非空。</summary>
    public bool HasFullClaim => _occupancy?.HasFullClaim ?? false;

    /// <summary>Listen 占用（仅顶层池，旁听只读消息流，多个可并存）。</summary>
    public IReadOnlyList<string> ListenSessionIds => _occupancy?.ListenSessionIds
        ?? Array.AsReadOnly(_childListeners.ToArray());

    public bool Listen(string sessionId)
    {
        if (_occupancy is not null) return _occupancy.Listen(sessionId);
        if (_childListeners.Contains(sessionId)) return false;
        _childListeners.Add(sessionId);
        return true;
    }

    public bool Unlisten(string sessionId) => _occupancy?.Unlisten(sessionId)
        ?? (_childListeners.RemoveAll(id => id == sessionId) != 0);

    /// <summary>Full claim 一个 session。已有占用返回 false。同 session 重入视为成功（幂等，供 TakeOver 后续接）。</summary>
    public bool Claim(string sessionId)
    {
        if (ActiveSessionId == sessionId) return true; // 幂等：compact/escalate TakeOver 后同 session 重入
        if (HasFullClaim) return false;
        if (_occupancy is not null)
        {
            if (!_occupancy.Claim(sessionId)) return false;
        }
        else _childActiveSessionId = sessionId;
        Members.Add(sessionId);
        return true;
    }

    /// <summary>同池换活跃 session（escalate/compact）：claim 保持，不释放不重取。旧 id 保留在 Members 供历史寻址。</summary>
    public void TakeOver(string newSessionId)
    {
        if (_occupancy is not null) _occupancy.TakeOver(newSessionId);
        else _childActiveSessionId = newSessionId;
        Members.Add(newSessionId);
    }

    /// <summary>释放某 session 的 Full claim（仅当它是当前活跃 session）。</summary>
    public bool Release(string sessionId)
    {
        if (ActiveSessionId == sessionId)
        {
            if (_occupancy is not null) return _occupancy.Release(sessionId);
            _childActiveSessionId = "";
            return true;
        }
        return false;
    }

    /// <summary>池是否空（无 Full claim 无 Listen，且无活跃子池）。空池可回收。</summary>
    public bool IsEmpty => !HasFullClaim && ListenSessionIds.Count == 0 && ChildPools.Count == 0;
}
