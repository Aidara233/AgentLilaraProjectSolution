using System.Collections.Generic;

namespace AgentCoreProcessor.Adapter
{
    public class ActionResult
    {
        public bool Success { get; init; }
        public string? Result { get; init; }
        public string? Error { get; init; }
    }

    public class AdapterAction
    {
        public string Name { get; init; } = "";
        public string Label { get; init; } = "";
        public string Description { get; init; } = "";
        public List<ActionParam> Params { get; init; } = new();
    }

    public class ActionParam
    {
        public string Name { get; init; } = "";
        public string Label { get; init; } = "";
        public string Type { get; init; } = "text";
        public List<string>? Options { get; init; }
        public bool Required { get; init; } = true;
    }
}
