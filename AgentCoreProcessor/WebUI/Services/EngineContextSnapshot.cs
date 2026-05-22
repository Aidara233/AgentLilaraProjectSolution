using System;
using System.Collections.Generic;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class EngineContextSnapshot
    {
        public int EstimatedTokens { get; init; }
        public int MessageCount { get; init; }
        public int ConversationOffset { get; init; }
        public CompressionTier CompressionTier { get; init; }
        public bool IsCompressing { get; init; }
        public string? Summary { get; init; }
        public int TotalRounds { get; init; }
        public bool IsInBackoff { get; init; }
        public List<ContextMessageSnapshot> Messages { get; init; } = new();
    }

    internal class ContextMessageSnapshot
    {
        public string Role { get; init; } = "";
        public string? Content { get; init; }
        public List<ContextPartSnapshot>? Parts { get; set; }
        public int EstimatedTokens { get; init; }
    }

    internal class ContextPartSnapshot
    {
        public string Type { get; init; } = "";
        public string? Text { get; init; }
        public string? ToolName { get; init; }
        public string? ToolInput { get; init; }
        public bool? IsError { get; init; }
    }
}
