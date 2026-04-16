using System.Collections.Generic;
using AgentCoreProcessor.Adapter;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// TopicEngine → WorkerEngine 的激活载荷。
    /// 包含消息批次和激活时刻的参与者快照。
    /// </summary>
    internal class ActivationBatch
    {
        /// <summary>缓冲窗口内的消息批次。</summary>
        public required List<(IncomingMessage Message, SessionContext Context)> Messages { get; init; }

        /// <summary>参与者快照（激活时刻的副本，Worker 处理期间不变）。</summary>
        public required Dictionary<int, ParticipantInfo> ParticipantSnapshot { get; init; }
    }
}
