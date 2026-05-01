using System.Collections.Generic;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 频道会话。包装现有 ChannelEngine，实现 IAgentSession。
    /// Phase 5: 基础包装，Phase 6 添加 WatchRules 支持。
    /// </summary>
    internal class ChannelSession : IAgentSession
    {
        private readonly ChannelEngine workerEngine;
        private readonly int channelId;

        public string SessionId => $"channel-{channelId}";
        public AgentSessionType Type => AgentSessionType.Channel;
        public bool IsAlive => workerEngine.IsAlive;

        public ChannelSession(ChannelEngine workerEngine, int channelId)
        {
            this.workerEngine = workerEngine;
            this.channelId = channelId;
        }

        /// <summary>
        /// 频道会话不接受系统循环指令（用户驱动，不是系统驱动）。
        /// </summary>
        public Task<bool> SendInstructionAsync(string instruction)
        {
            return Task.FromResult(false);
        }

        /// <summary>
        /// 更新关注规则（Phase 6 实现）。
        /// </summary>
        public Task<bool> UpdateWatchRulesAsync(List<WatchRule> rules)
        {
            // Phase 6: 调用 workerEngine.UpdateWatchRules(rules)
            // Phase 5: 暂时返回 true
            return Task.FromResult(true);
        }

        /// <summary>
        /// 请求停止频道会话。
        /// </summary>
        public void RequestStop()
        {
            workerEngine.RequestStop();
        }
    }
}
