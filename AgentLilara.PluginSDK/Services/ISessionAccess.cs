// AgentLilara.PluginSDK/Services/ISessionAccess.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 全局 session 状态查询与 session 间消息接口。
    /// 由会话服务实现；工具宿主通过此接口发现服务并转发。
    /// </summary>
    public interface ISessionAccess
    {
        /// <summary>列出所有活跃 session 的快照（含子 agent / task）。已完成/崩溃存档不出现。</summary>
        Task<List<SessionSummary>> ListSessionsAsync();

        /// <summary>
        /// 向指定 session 发送一条消息：即时注入目标上下文（带来源标记）并唤醒。
        /// 目标不存在或未活跃返回 false。
        /// </summary>
        /// <param name="targetSessionId">目标 sessionId（裸，无前缀）。</param>
        /// <param name="senderSessionId">发送方 sessionId（裸，无前缀），用于来源标记。</param>
        /// <param name="content">消息内容。</param>
        bool SendMessageAsync(string targetSessionId, string senderSessionId, string content);
    }

    /// <summary>session 快照信息（list_sessions 返回）。</summary>
    public class SessionSummary
    {
        public string SessionId { get; set; } = "";
        public string Type { get; set; } = "";
        public string Status { get; set; } = "";
        /// <summary>占用频道名（仅占用频道的会话有值，子 agent/task 为空）。</summary>
        public string? Channel { get; set; }
        /// <summary>父 sessionId（仅子 agent 有值，顶层会话为空）。</summary>
        public string? Parent { get; set; }
        public int Rounds { get; set; }
        public DateTime LastActive { get; set; }
        public string Core { get; set; } = "";
    }
}
