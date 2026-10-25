using AgentCoreProcessor.Core;
using AgentLilara.SessionBrain;

namespace AgentCoreProcessor.Engine.Session.Brain;

/// <summary>
/// 进程级接缝运行器。⚠️ 默认 <see cref="SessionBrainHost.Off"/>：
/// 没有显式配置时，会话模型面**不经过**任何内核代码。
/// </summary>
internal static class SessionBrainRuntime
{
    private static SessionBrainHost _host = SessionBrainHost.Off;

    public static SessionBrainHost Host
    {
        get => Volatile.Read(ref _host);
        set => Volatile.Write(ref _host, value ?? SessionBrainHost.Off);
    }

    /// <summary>
    /// Legacy 调用**刚刚成功返回**时调用：同步取快照（下一步会重设客户端历史），再把比较丢到后台。
    /// ⚠️ 这里的任何失败都只报告，绝不影响会话。
    /// </summary>
    public static void Observe(AgentCore core, string sessionId, string engineType, string workingDirectory)
    {
        var host = Host;
        if (!host.IsEnabled) return;

        try
        {
            host.RunShadow(LegacyRequestMapper.Snapshot(core, sessionId, engineType, workingDirectory));
        }
        catch (Exception ex)
        {
            host.OnFailure?.Invoke("影子请求构造失败。", ex);
        }
    }
}
