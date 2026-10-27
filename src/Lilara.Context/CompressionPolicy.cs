using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
    internal class CompressionPolicy
    {
        private readonly CompressionThresholds _config;
        private readonly Action _onL3Triggered;
        private readonly int _l1Tokens;

        private bool _l1Injected;
        private CompressionTier _currentTier = CompressionTier.None;

        public CompressionTier CurrentTier => _currentTier;

        public CompressionPolicy(CompressionThresholds config, Action onL3Triggered,
            int? l1Tokens = null)
        {
            _config = config;
            _onL3Triggered = onL3Triggered;
            // L1 首次提醒阈值可覆盖（express 轻量会话按 Flash 模型窗口调低，其余类型沿用 config）
            _l1Tokens = l1Tokens ?? config.CompressL1Tokens;
        }

        public CompressionTier Evaluate(int estimatedTokens)
        {
            var prev = _currentTier;
            if (estimatedTokens < _config.CompressMinTokens)
                _currentTier = CompressionTier.None;
            else if (estimatedTokens >= _config.CompressL3Tokens)
                _currentTier = CompressionTier.L3;
            else if (estimatedTokens >= _config.CompressL2Tokens)
                _currentTier = CompressionTier.L2;
            else if (estimatedTokens >= _l1Tokens)
                _currentTier = CompressionTier.L1;
            else
                _currentTier = CompressionTier.None;

            // L3 触发回调：通知 RuntimeSession 在下一轮锁定工具表
            if (_currentTier == CompressionTier.L3 && prev != CompressionTier.L3)
                _onL3Triggered?.Invoke();

            return _currentTier;
        }

        public string? GetInjectText(int estimatedTokens)
        {
            _ = Evaluate(estimatedTokens);
            return _currentTier switch
            {
                CompressionTier.L1 when !_l1Injected =>
                    "[系统提示] 上下文稍大。如果当前内容不重要，可以考虑调用 compact 工具来精简会话。\n" +
                    "调用时需提供一段摘要作为参数，描述：做了什么、得出了什么、下一步要做什么。",
                CompressionTier.L2 =>
                    "[系统警告] 会话长度已经较高，建议尽快在合适的地方调用 compact 工具。\n" +
                    "compact 参数应是你对当前会话的完整总结。调用后会启动新会话承接你的摘要。",
                CompressionTier.L3 =>
                    "[系统强制] 上下文已达硬上限，本轮请调用 compact 工具结束当前会话并切换到新会话。\n" +
                    "参数是你对会话的完整总结。请认真总结，新会话将基于你的总结继续工作。",
                _ => null
            };
        }

        public void MarkL1Injected() => _l1Injected = true;

    }

    internal sealed record CompressionThresholds(int CompressMinTokens, int CompressL1Tokens, int CompressL2Tokens, int CompressL3Tokens);

    internal enum CompressionTier { None, L1, L2, L3 }
}