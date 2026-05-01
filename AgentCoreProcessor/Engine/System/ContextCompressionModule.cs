using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 上下文压缩模块。监控 token 使用，触发压缩。
    /// </summary>
    internal class ContextCompressionModule : EngineModule
    {
        public override string Name => "上下文压缩";
        public override int PromptPriority => 100; // 最低优先级，不注入 prompt

        private const int CompressionThreshold = 80000; // 80k tokens
        private const int CompressionTarget = 30000;    // 压缩到 30k
        private const int RecentRoundsToKeep = 5;

        private readonly SummarizationCore summarizationCore = new();
        private readonly ContextPersistence persistence;
        private List<Message> conversationHistory = new();
        private string? currentSummary;

        public ContextCompressionModule(ContextPersistence persistence)
        {
            this.persistence = persistence;
        }

        public override void Attach(ILoopBus bus)
        {
            // 每轮结束后检查是否需要压缩
            bus.Subscribe<RoundCompletedEvent>(async e =>
            {
                conversationHistory.AddRange(e.Messages);
                await CheckAndCompressAsync();
            });
        }

        /// <summary>
        /// 添加消息到历史（供 SystemEngine 调用）。
        /// </summary>
        public void AddMessages(List<Message> messages)
        {
            conversationHistory.AddRange(messages);
        }

        /// <summary>
        /// 获取当前上下文（供 SystemEngine 构建 prompt）。
        /// </summary>
        public List<Message> GetContext()
        {
            var context = new List<Message>();

            // 如果有摘要，添加到开头
            if (!string.IsNullOrEmpty(currentSummary))
            {
                context.Add(new Message
                {
                    Role = "user",
                    Content = $"[上下文摘要]\n{currentSummary}"
                });
            }

            // 添加完整历史
            context.AddRange(conversationHistory);

            return context;
        }

        /// <summary>
        /// 加载持久化的上下文。
        /// </summary>
        public void LoadPersistedContext()
        {
            var (summary, rounds) = persistence.LoadContext();
            currentSummary = summary;

            foreach (var round in rounds)
            {
                conversationHistory.AddRange(round);
            }

            if (conversationHistory.Any())
            {
                FrameworkLogger.Log("ContextCompression", $"已恢复 {conversationHistory.Count} 条消息");
            }
        }

        private async Task CheckAndCompressAsync()
        {
            // 粗略估算 token（字符数 / 3）
            var totalChars = conversationHistory.Sum(m => m.Content?.Length ?? 0);
            var estimatedTokens = totalChars / 3;

            if (estimatedTokens < CompressionThreshold)
            {
                return;
            }

            FrameworkLogger.Log("ContextCompression", $"触发压缩: 估算 {estimatedTokens} tokens");

            // 保留最近 N 轮
            var recentMessages = conversationHistory.TakeLast(RecentRoundsToKeep).ToList();
            var toCompress = conversationHistory.Take(conversationHistory.Count - RecentRoundsToKeep).ToList();

            if (toCompress.Count == 0)
            {
                FrameworkLogger.Log("ContextCompression", "无需压缩（历史太短）");
                return;
            }

            // 调用摘要 Core
            currentSummary = await summarizationCore.SummarizeContextAsync(toCompress, currentSummary);

            // 更新历史：只保留最近 N 轮
            conversationHistory = recentMessages;

            // 持久化
            persistence.SaveSummaryAndClearContext(currentSummary);

            var newEstimatedTokens = (currentSummary.Length + conversationHistory.Sum(m => m.Content?.Length ?? 0)) / 3;
            FrameworkLogger.Log("ContextCompression", $"压缩完成: {estimatedTokens} → {newEstimatedTokens} tokens");
        }

        public override string? BuildPromptSection(EngineMode mode)
        {
            // 不注入 prompt，上下文通过 GetContext() 获取
            return null;
        }
    }

    /// <summary>
    /// 一轮完成事件（供压缩模块订阅）。
    /// </summary>
    internal class RoundCompletedEvent
    {
        public List<Message> Messages { get; set; } = new();
    }
}
