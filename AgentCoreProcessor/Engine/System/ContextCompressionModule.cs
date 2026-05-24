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

    }
}
