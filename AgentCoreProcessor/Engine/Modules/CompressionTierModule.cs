using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 三层压缩判定模块。仅做 Evaluate + GetInjectText：
    /// - L1（80k，约占 200k 上下文 40%）：温和提醒，不强制
    /// - L2（130k，约占 65%）：明确催促，但仍允许模型在合适时机执行
    /// - L3（150k，约占 75%）：强制锁定工具表只剩 compact（由 RuntimeSession 的 _l3Lockdown 处理）
    ///
    /// 压缩动作由模型调 CompactTool 触发（新 session 接管旧 session），本模块不再调用 SummarizationCore。
    /// </summary>
    internal class CompressionTierModule
    {
        private readonly CompressionPolicy _policy;

        public CompressionTier CurrentTier => _policy.CurrentTier;

        public CompressionTierModule(AgentConfig config, Func<List<Message>> getHistory, Action onL3Triggered, int? l1Tokens = null)
        {
            _policy = new CompressionPolicy(new CompressionThresholds(config.CompressMinTokens,
                config.CompressL1Tokens, config.CompressL2Tokens, config.CompressL3Tokens), onL3Triggered, l1Tokens);
        }

        public CompressionTier Evaluate(int estimatedTokens) => _policy.Evaluate(estimatedTokens);
        public string? GetInjectText(int estimatedTokens) => _policy.GetInjectText(estimatedTokens);
        public void MarkL1Injected() => _policy.MarkL1Injected();

        /// <summary>估算 Message 字符数。供外部估算 tokens 用（chars/3）。</summary>
        public static int EstimateChars(Message m)
        {
            if (m.Content != null)
                return m.Content.Length;
            if (m.ContentParts != null)
                return m.ContentParts.Sum(p =>
                    (p.Text?.Length ?? 0) + (p.ToolInput?.Length ?? 0) + (p.ToolName?.Length ?? 0));
            return 0;
        }
    }

}