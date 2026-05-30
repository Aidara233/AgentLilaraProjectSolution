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
        private readonly List<Message> _history;
        private readonly Action<string, List<Message>> _onComplete;

        public string Name => "compress";
        public string Description => "压缩当前对话历史。保留最近对话并生成摘要，释放上下文空间。";
        public IReadOnlyList<ToolParameter> Parameters { get; } = new List<ToolParameter>();
        public TimeSpan Timeout => TimeSpan.FromSeconds(60);

        public CompressTool(CompressionTierModule compression, List<Message> history,
            Action<string, List<Message>> onComplete)
        {
            _compression = compression;
            _history = history;
            _onComplete = onComplete;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            await _compression.CompressAsync(new List<Message>(_history), _onComplete);
            return new ToolResult { Status = "success", Data = "压缩完成。" };
        }
    }
}
