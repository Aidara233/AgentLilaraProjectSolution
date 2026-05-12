using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 统一 agent 间通信接口。替代旧的 NotifyChannel/SendToSubAgent 分散机制。
    /// 频道循环 sessionId 约定格式：channel:{channelId}
    /// </summary>
    public interface IAgentMessaging
    {
        /// <summary>向任意 agent 发送消息。</summary>
        void Send(string sessionId, AgentMessage message, bool wake = true);

        /// <summary>发送并等待回复（委托模式）。</summary>
        Task<AgentMessage?> SendAndWait(string sessionId, AgentMessage message, TimeSpan timeout);

        /// <summary>获取当前 agent 的待处理消息。</summary>
        List<AgentMessage> Receive(string sessionId, int maxCount = 10);
    }

    public class AgentMessage
    {
        /// <summary>消息类型：notification / instruction / delegation / result</summary>
        public string Type { get; set; } = "notification";

        /// <summary>消息内容。</summary>
        public string Content { get; set; } = "";

        /// <summary>发送方 sessionId。</summary>
        public string? SourceSessionId { get; set; }

        /// <summary>附加元数据。</summary>
        public Dictionary<string, string>? Metadata { get; set; }

        /// <summary>消息时间戳。</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
