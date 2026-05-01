using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// Agent 会话类型。
    /// </summary>
    public enum AgentSessionType
    {
        /// <summary>频道会话（长期运行，对应 WorkerEngine）。</summary>
        Channel,

        /// <summary>任务会话（一次性，用完销毁）。</summary>
        Task,

        /// <summary>监控会话（长期运行，被动监听）。</summary>
        Monitor
    }

    /// <summary>
    /// Agent 会话接口。统一抽象频道循环、任务子 agent、监控 agent。
    /// </summary>
    public interface IAgentSession
    {
        /// <summary>会话 ID（全局唯一）。</summary>
        string SessionId { get; }

        /// <summary>会话类型。</summary>
        AgentSessionType Type { get; }

        /// <summary>是否存活。</summary>
        bool IsAlive { get; }

        /// <summary>
        /// 发送指令给 agent（系统循环 → 子 agent）。
        /// 频道会话不支持此操作（返回 false）。
        /// </summary>
        Task<bool> SendInstructionAsync(string instruction);

        /// <summary>
        /// 更新关注规则（系统循环 → 频道会话）。
        /// 任务会话不支持此操作（返回 false）。
        /// </summary>
        Task<bool> UpdateWatchRulesAsync(List<WatchRule> rules);

        /// <summary>请求停止会话。</summary>
        void RequestStop();
    }

    /// <summary>
    /// 关注规则（Phase 6 实现）。
    /// </summary>
    public class WatchRule
    {
        public string RuleId { get; set; } = "";
        public string Description { get; set; } = "";
        public string Pattern { get; set; } = "";
        public WatchAction Action { get; set; }
        public bool AutoResponse { get; set; }
    }

    public enum WatchAction
    {
        Notify,      // 仅通知
        Interrupt,   // 打断当前任务
        Escalate     // 升级到系统循环
    }
}
