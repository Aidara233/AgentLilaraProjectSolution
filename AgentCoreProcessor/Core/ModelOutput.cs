using System.Collections.Generic;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    internal readonly struct ModelOutput
    {
        public string? Text { get; init; }
        public string? Thinking { get; init; }
        public List<ToolCall>? ToolCalls { get; init; }
        public bool IsText => Text != null;

        public static ModelOutput FromText(string text) => new() { Text = text };
        public static ModelOutput FromTools(List<ToolCall> calls, string? thinking = null)
            => new() { ToolCalls = calls, Thinking = thinking };
    }
}
