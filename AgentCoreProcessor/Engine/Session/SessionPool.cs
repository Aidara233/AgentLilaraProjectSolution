// AgentCoreProcessor/Engine/Session/SessionPool.cs
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 资源池。会话体系的统一抽象：一个频道 = 一个顶层池，持有频道占用（Full claim）与当前活跃 session。
/// 一个池只能有一个活跃 session：express escalate 到 working / compact 时**同池换活跃 session**，
/// 占用状态原地不动（不释放、不重取）——这是"占用状态在池里"的核心语义。
/// 子 agent = 子池（阶段 2 扩展 Parent/Children 层级）。
/// </summary>
internal sealed class SessionPool
{
    public SessionPool(int channelId)
    {
        ChannelId = channelId;
    }

    /// <summary>顶层池对应的频道。</summary>
    public int ChannelId { get; }

    /// <summary>当前占用该频道的活跃 session（express 或 working）。空 = 无 Full 占用。</summary>
    public string ActiveSessionId { get; private set; } = "";

    /// <summary>是否持有 Full claim（有活跃 session 即占用）。</summary>
    public bool HasFullClaim => !string.IsNullOrEmpty(ActiveSessionId);

    /// <summary>Listen 占用（旁听 session，只读消息流，多个可并存）。</summary>
    public List<string> ListenSessionIds { get; } = new();

    /// <summary>Full claim 一个 session。已有占用返回 false。</summary>
    public bool Claim(string sessionId)
    {
        if (HasFullClaim) return false;
        ActiveSessionId = sessionId;
        return true;
    }

    /// <summary>同池换活跃 session（escalate/compact）：claim 保持，不释放不重取。</summary>
    public void TakeOver(string newSessionId)
    {
        ActiveSessionId = newSessionId;
    }

    /// <summary>释放某 session 的 Full claim（仅当它是当前活跃 session）。</summary>
    public bool Release(string sessionId)
    {
        if (ActiveSessionId == sessionId)
        {
            ActiveSessionId = "";
            return true;
        }
        return false;
    }

    /// <summary>池是否空（无 Full claim 无 Listen）。空池可回收。</summary>
    public bool IsEmpty => !HasFullClaim && ListenSessionIds.Count == 0;
}
