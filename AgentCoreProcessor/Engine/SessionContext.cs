using System.Collections.Generic;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 一次消息处理所需的会话上下文，由 SessionManager 构建后传递给 WorkerEngine。
    /// </summary>
    internal class SessionContext
    {
        /// <summary>发送消息的用户（内部实体，账号级）</summary>
        public required User User { get; set; }

        /// <summary>用户对应的自然人</summary>
        public required Person Person { get; set; }

        /// <summary>消息所属频道</summary>
        public required Channel Channel { get; set; }

        /// <summary>消息被归类到的话题</summary>
        public required Topic Topic { get; set; }

        /// <summary>当前话题的最近历史消息（按时间升序）</summary>
        public List<UserMessage> RecentMessages { get; set; } = new();
    }
}
