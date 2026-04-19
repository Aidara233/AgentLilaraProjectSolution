using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgentCoreProcessor.Memory;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 记忆窗口模块。持有当前活跃记忆，注入 prompt。
    /// 记忆检索仍由 WorkerEngine 驱动（依赖 MemoryService），本模块只负责格式化注入。
    /// </summary>
    internal class MemoryWindowModule : EngineModule
    {
        public override string Name => "记忆窗口";
        public override int PromptPriority => 40;

        private List<ScoredMemory>? activeMemories;

        /// <summary>由 WorkerEngine 在每轮准备阶段设置。</summary>
        public void SetMemories(List<ScoredMemory>? memories)
        {
            activeMemories = memories;
        }

        public override void Attach(ILoopBus bus) { }

        public override string? BuildPromptSection(EngineMode mode)
        {
            return FormatMemory(activeMemories, mode == EngineMode.Express ? 5 : 10);
        }

        private static string? FormatMemory(List<ScoredMemory>? results, int topK)
        {
            if (results == null || results.Count == 0) return null;
            var items = results.Where(m => !m.IsPersona).Take(topK).ToList();
            if (items.Count == 0) return null;

            var sb = new StringBuilder("[记忆参考]\n");
            foreach (var m in items)
            {
                if (m.Confidence == "low")
                    sb.AppendLine($"- {m.Content}（不太确定）");
                else
                    sb.AppendLine($"- {m.Content}");
            }
            return sb.ToString().TrimEnd();
        }

        public override void Reset()
        {
            activeMemories = null;
        }
    }
}
