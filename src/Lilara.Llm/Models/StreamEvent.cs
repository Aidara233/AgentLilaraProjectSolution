namespace AgentCoreProcessor.Models
{
    public enum StreamEventType
    {
        Text,
        Thinking,
        ToolUseStart,
        ToolUseDelta,
        ToolUseEnd,
        Usage
    }

    public class StreamEvent
    {
        public StreamEventType Type { get; init; }
        public string? Content { get; init; }
        public string? ToolUseId { get; init; }
        public string? ToolName { get; init; }
        public Usage? Usage { get; init; }
    }
}
