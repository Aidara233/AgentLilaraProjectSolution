using System.Collections.Generic;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 复盘核心。继承 CoreBase 获取 LLM 通信能力（GenerateAsync + break 解析）。
    /// 由 ReviewEngine 在 Agent 循环中使用。
    /// </summary>
    internal class ReviewCore : CoreBase
    {
        /// <summary>清除并设置对话历史，供 ReviewEngine 外部调用。</summary>
        public void SetConversation(List<Message> messages)
        {
            processor.Client.ClearConversationHistory();
            processor.Client.SetConversationHistory(messages);
        }
    }
}
