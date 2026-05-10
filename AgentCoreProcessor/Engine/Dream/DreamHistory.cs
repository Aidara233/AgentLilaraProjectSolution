using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine
{
    internal class FragmentDetailRecord
    {
        public string Action { get; set; } = "";
        public int? MemoryId { get; set; }
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
        public string? Note { get; set; }
    }

    internal class FragmentRecord
    {
        public string Type { get; set; } = "";
        public DateTime StartTime { get; set; }
        public double DurationSeconds { get; set; }
        public bool Success { get; set; } = true;
        public string? Summary { get; set; }
        public string? InputMemoryIds { get; set; }
        public string? OutputRaw { get; set; }
        public List<FragmentDetailRecord> Details { get; set; } = new();
    }
}
