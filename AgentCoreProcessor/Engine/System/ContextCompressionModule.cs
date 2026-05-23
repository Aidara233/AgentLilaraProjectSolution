using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 上下文压缩模块。由 SystemEngine 主动调用压缩，不再被动监听。
    /// </summary>
    internal class ContextCompressionModule : EngineModule
    {
        public override string Name => "上下文压缩";

        private const int RecentRoundsToKeep = 10;

        private readonly SummarizationCore summarizationCore = new();
        private readonly ContextPersistence persistence;
        private string? currentSummary;
        private List<Message> keptMessages = new();

        public ContextCompressionModule(ContextPersistence persistence)
        {
            this.persistence = persistence;
        }

        public override void Attach(ILoopBus bus)
        {
            // 不再被动订阅 — 由 SystemEngine 主动调用 CompressAsync
        }

        public void SetSummary(string? summary)
        {
            currentSummary = summary;
        }

        public string? GetSummary() => currentSummary;

        public List<Message> GetKeptMessages() => keptMessages;

        /// <summary>
        /// 执行压缩：保留最近 N 条消息，其余压缩为摘要。
        /// </summary>
        public async Task CompressAsync(List<Message> fullHistory)
        {
            if (fullHistory.Count <= RecentRoundsToKeep)
            {
                keptMessages = new List<Message>(fullHistory);
                return;
            }

            var toCompress = fullHistory.Take(fullHistory.Count - RecentRoundsToKeep).ToList();
            keptMessages = fullHistory.TakeLast(RecentRoundsToKeep).ToList();


            currentSummary = await summarizationCore.SummarizeContextAsync(toCompress, currentSummary);

        }
    }

    /// <summary>
    /// 一轮完成事件（供其他模块订阅）。
    /// </summary>
    internal class RoundCompletedEvent
    {
        public List<Message> Messages { get; set; } = new();
    }
}
