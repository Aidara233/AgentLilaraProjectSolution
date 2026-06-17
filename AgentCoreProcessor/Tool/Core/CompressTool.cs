using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Models;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    [ToolMeta(EngineTypes = new[] { "channel", "system" })]
    internal class CompressTool : ITool
    {
        private readonly CompressionTierModule _compression;
        private readonly Func<List<Message>> _getHistory;
        private readonly Action<string, List<Message>> _onComplete;

        public string Name => "compress";
        public string Description => "压缩当前对话历史。保留最近对话并生成摘要，释放上下文空间。";
        public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>();
        public TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public CompressTool(CompressionTierModule compression, Func<List<Message>> getHistory,
            Action<string, List<Message>> onComplete)
        {
            _compression = compression;
            _getHistory = getHistory;
            _onComplete = onComplete;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var historyClone = new List<Message>(_getHistory());
            // 排除当前轮的 assistant 消息（即 compress 工具自身的 tool_use），
            // 避免压缩产物中包含孤儿 tool_use（对应的 tool_result 将在回调后被 Agent 循环追加）
            if (historyClone.Count > 0 && historyClone[^1].Role == "assistant")
                historyClone.RemoveAt(historyClone.Count - 1);
            await _compression.CompressAsync(historyClone, _onComplete);
            return new ToolResult { Status = "success", Data = "压缩完成。" };
        }
    }
}
