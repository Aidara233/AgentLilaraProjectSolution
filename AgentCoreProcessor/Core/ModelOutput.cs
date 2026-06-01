using System.Collections.Generic;
using AgentCoreProcessor.Tool;

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
        public static ModelOutput FromExpressWithTools(string text, List<ToolCall>? calls, string? thinking = null, Models.Usage? usage = null)
            => new() { Text = text, ToolCalls = calls, Thinking = thinking, Usage = usage };
    }
}
