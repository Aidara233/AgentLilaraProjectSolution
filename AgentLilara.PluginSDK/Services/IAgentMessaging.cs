using System;
using System.Collections.Generic;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// 循环间单发消息接口。所有跨循环消息的唯一入口。
    /// 消息模型：fire-and-forget，无状态机、无 response 概念。
    /// 回复就是再发一条新消息（用对方的 loopId 作 target_id）。
    /// </summary>
    public interface IAgentMessaging
    {
        /// <summary>
        /// 向目标循环发送一条消息。target_id 为 null 表示广播给所有活跃循环。
        /// 立即投递到内存队列并唤醒目标（如目标活跃），不持久化、无回应。
        /// 返回 messageId（仅供发起者日志/排除用，无法用它做查询）。
        /// </summary>
        string SendMessage(string? targetId, string content);

        /// <summary>读取并清空本循环待处理的消息（手动查看收件箱）。</summary>
        List<LoopMessageInfo> Receive(int maxCount = 10);

        /// <summary>排出待注入的通知消息（由通知模块消费）。</summary>
        List<LoopMessageInfo> DrainNotifications();

        /// <summary>获取所有活跃循环的 ID 列表。</summary>
        List<string> GetActiveLoopIds();
    }

    /// <summary>循环消息 DTO。</summary>
    public class LoopMessageInfo
    {
        public string MessageId { get; set; } = "";
        public string InitiatorId { get; set; } = "";
        /// <summary>null 表示广播。</summary>
        public string? TargetId { get; set; }
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    /// <summary>旧版 AgentMessage，保留兼容。</summary>
    public class AgentMessage
    {
        public string Type { get; set; } = "notification";
        public string Content { get; set; } = "";
        public string? SourceSessionId { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}