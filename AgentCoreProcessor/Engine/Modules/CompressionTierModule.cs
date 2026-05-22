using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine.Modules
{
    internal class CompressionTierModule
    {
        private readonly AgentConfig _config;
        private readonly SummarizationCore _summarizer = new();
        private readonly Func<List<Message>> _getHistory;
        private readonly Action _onL3Triggered;

        private string? _currentSummary;
        private bool _l1Injected;
        private CompressionTier _currentTier = CompressionTier.None;
        private volatile bool _compressing;

        public CompressionTier CurrentTier => _currentTier;
        public string? CurrentSummary => _currentSummary;
        public bool IsCompressing => _compressing;

        public CompressionTierModule(AgentConfig config, Func<List<Message>> getHistory, Action onL3Triggered)
        {
            _config = config;
            _getHistory = getHistory;
            _onL3Triggered = onL3Triggered;
        }

        public void SetSummary(string? summary) => _currentSummary = summary;

        public CompressionTier Evaluate(int estimatedTokens)
        {
            if (estimatedTokens < _config.CompressMinTokens)
                _currentTier = CompressionTier.None;
            else if (estimatedTokens >= _config.CompressL3Tokens)
                _currentTier = CompressionTier.L3;
            else if (estimatedTokens >= _config.CompressL2Tokens)
                _currentTier = CompressionTier.L2;
            else if (estimatedTokens >= _config.CompressL1Tokens)
                _currentTier = CompressionTier.L1;
            else
                _currentTier = CompressionTier.None;
            return _currentTier;
        }

        public string? GetInjectText(int estimatedTokens)
        {
            _ = Evaluate(estimatedTokens);
            return _currentTier switch
            {
                CompressionTier.L1 when !_l1Injected =>
                    "[压缩提示] 上下文较长（首次提醒）。如果当前话题不重要（如闲聊），可以调用 `compress` 工具压缩对话历史。",
                CompressionTier.L2 =>
                    "[压缩提示] 上下文已较长，请尽快调用 `compress` 工具压缩上下文，腾出空间。",
                CompressionTier.L3 =>
                    "这是压缩前最后一轮对话，本轮结束后将强制压缩。请简要告知用户。",
                _ => null
            };
        }

        public void MarkL1Injected() => _l1Injected = true;

        public async Task CompressAsync(List<Message> history, Action<string, List<Message>> onComplete)
        {
            if (_compressing) return;
            _compressing = true;
            try
            {
                var totalTokens = history.Sum(m => EstimateChars(m)) / 3;
                using var span = Signal.Open(LogGroup.Engine, $"上下文压缩 ({totalTokens}t, {_currentTier})",
                    new { historyCount = history.Count, estimatedTokens = totalTokens, tier = _currentTier.ToString(), hasPriorSummary = _currentSummary != null });

                var retained = new List<Message>();
                int tokenCount = 0;
                for (int i = history.Count - 1; i >= 0; i--)
                {
                    var t = EstimateChars(history[i]) / 3;
                    if (retained.Count >= _config.CompressRetainedMessageCount
                        || tokenCount + t > _config.CompressRetainedMaxTokens)
                        break;
                    retained.Insert(0, history[i]);
                    tokenCount += t;
                }

                var toCompress = history.Take(history.Count - retained.Count).ToList();
                if (toCompress.Count > 0
                    && toCompress.Sum(m => EstimateChars(m)) / 3 >= _config.CompressMinTokens)
                {
                    _currentSummary = await _summarizer.SummarizeContextAsync(toCompress, _currentSummary);
                }

                _l1Injected = false;
                span.SetCloseDetail(new
                {
                    compressedCount = toCompress.Count,
                    retainedCount = retained.Count,
                    retainedTokens = tokenCount,
                    summaryLength = _currentSummary?.Length ?? 0,
                    summary = _currentSummary
                });
                onComplete(_currentSummary ?? "", retained);
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "压缩失败", new { error = ex.GetType().Name, message = ex.Message });
            }
            finally
            {
                _compressing = false;
            }
        }

        /// <summary>同步压缩（L3 硬保底）。</summary>
        public Task CompressSyncAsync(List<Message> history, Action<string, List<Message>> onComplete)
            => CompressAsync(history, onComplete);

        private static int EstimateChars(Message m)
        {
            if (m.Content != null)
                return m.Content.Length;
            if (m.ContentParts != null)
                return m.ContentParts.Sum(p =>
                    (p.Text?.Length ?? 0) + (p.ToolInput?.Length ?? 0) + (p.ToolName?.Length ?? 0));
            return 0;
        }
    }

    internal enum CompressionTier { None, L1, L2, L3 }
}
