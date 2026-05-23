using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class LogEntry
    {
        public DateTime Time { get; init; }
        public string Source { get; init; } = "";
        public string Message { get; init; } = "";
        public bool IsError { get; init; }
    }

    internal class LogStreamService : IDisposable
    {
        private readonly ConcurrentQueue<LogEntry> buffer = new();
        private const int MaxBufferSize = 2000;

        public List<LogEntry> GetRecent(int count, string? sourceFilter = null)
        {
            var items = buffer.ToArray().AsEnumerable();
            if (!string.IsNullOrEmpty(sourceFilter))
                items = items.Where(e => e.Source.Contains(sourceFilter, StringComparison.OrdinalIgnoreCase));
            return items.TakeLast(count).ToList();
        }

        public void Dispose() { }
    }
}
