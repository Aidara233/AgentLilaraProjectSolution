using System.Collections.Generic;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    internal readonly struct ModelOutput
    {
        public string? Text { get; init; }
        public List<ToolCall>? ToolCalls { get; init; }
        public bool IsText => Text != null;

        public static ModelOutput FromText(string text) => new() { Text = text };
        public static ModelOutput FromTools(List<ToolCall> calls) => new() { ToolCalls = calls };
    }
}
