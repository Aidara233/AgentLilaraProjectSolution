using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// Agent 宿主接口。Engine 实现，Agent 通过此接口拉取上下文注入。
    /// </summary>
    internal interface IAgentHost
    {
        /// <summary>每次 Agent.RunAsync() 启动时调一次。新消息、压缩产物等一次性内容。</summary>
        Task<List<Message>?> BuildStartInjectAsync();

        /// <summary>Agent 每轮调一次。轮次提示、压缩提醒、模式说明等持续内容。</summary>
        Task<List<Message>?> BuildRoundInjectAsync();

        /// <summary>
        /// BuildStartInjectAsync 返回的消息中，前 N 条为框架消息（不持久化）。
        /// 之后的消息为对话内容（需持久化）。默认 0 表示全部为对话。
        /// </summary>
        int FrameworkMessageCount => 0;
    }
}
