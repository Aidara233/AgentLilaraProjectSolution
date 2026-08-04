// AgentLilara.PluginSDK/Services/ISessionResourceAccess.cs
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// session-scoped 子资源接口：资源归属某个 session，session compact 时被通知 rebind，
    /// session 死亡时被通知清理。实装者：SubAgentAccessAdapter、DownloadStore、SshGlobalComponent。
    /// </summary>
    public interface ISessionResource
    {
        /// <summary>所属 session 即将 compact 到 newSessionId。资源应改自己的 loopId 到 newSessionId。</summary>
        Task OnSessionCompactingAsync(string oldSessionId, string newSessionId);

        /// <summary>所属 session 已结束 / 被 ForceKill。资源应清理挂在该 session 上的状态。</summary>
        Task OnSessionFreedAsync(string sessionId);

        /// <summary>是否有挂在该 sessionId 上仍未完成的资源。用于 end_session 阻塞判定。</summary>
        bool HasActiveResources(string sessionId);
    }

    /// <summary>
    /// SessionEngine 暴露的子资源注册表。供插件 GlobalComponent 注册自己的 ISessionResource。
    /// </summary>
    public interface ISessionResourceAccess
    {
        /// <summary>注册一个 session-scoped 子资源。资源在 session compact/死亡时被回调。</summary>
        void Register(ISessionResource resource);

        /// <summary>反注册（资源自身关闭时调用）。</summary>
        void Unregister(ISessionResource resource);

        /// <summary>
        /// 跨 0 或多次 compact 转发，解析会话引用（裸 sessionId）当前对应的活跃 session ID。
        /// 若引用是已死的 session（不在 _sessions 且不在池成员），返回 null。
        /// </summary>
        string? ResolveActiveSession(string sessionId);
    }
}
