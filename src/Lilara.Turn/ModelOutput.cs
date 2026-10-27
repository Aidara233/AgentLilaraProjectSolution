using System.Collections.Generic;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Core
{
    internal readonly struct ModelOutput
    {
        public string? Text { get; init; }
        public string? Thinking { get; init; }
        public List<ToolCall>? ToolCalls { get; init; }
        public Models.Usage? Usage { get; init; }

        public bool IsText => Text != null;
        public bool HasToolCalls => ToolCalls != null && ToolCalls.Count > 0;

        public static ModelOutput FromText(string text) => new() { Text = text };
        public static ModelOutput FromTools(List<ToolCall> calls, string? thinking = null, Models.Usage? usage = null)
            => new() { ToolCalls = calls, Thinking = thinking, Usage = usage };
    }
}
