using System.Collections.Generic;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 子 agent 核心。继承 CoreBase 获取 LLM 通信能力。
    /// 不注入人设，纯任务执行者。由 SubAgentRunner 在 Agent 循环中使用。
    /// </summary>
    internal class SubAgentCore : CoreBase
    {
        protected override bool UsePersona => false;

        /// <summary>清除并设置对话历史，供外部循环调用。</summary>
        public void SetConversation(List<Message> messages)
        {
            processor.Client.ClearConversationHistory();
            processor.Client.SetConversationHistory(messages);
        }
    }
}
