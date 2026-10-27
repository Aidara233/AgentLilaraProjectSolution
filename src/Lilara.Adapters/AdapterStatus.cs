using System;

namespace AgentCoreProcessor.Adapter
{
    public enum AdapterConnectionState
    {
        Stopped,
        Connecting,
        Connected,
        Reconnecting,
        Error
    }

    public class AdapterStatus
    {
        public string Id { get; init; } = "";
        public string Platform { get; init; } = "";
        public bool Enabled { get; init; }
        public AdapterConnectionState State { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? LastMessageSentAt { get; init; }
        public DateTime? LastMessageReceivedAt { get; init; }
        public long MessagesSent { get; init; }
        public long MessagesReceived { get; init; }
        public int ReconnectCount { get; init; }
        public string? LastError { get; init; }
        public DateTime? LastErrorAt { get; init; }
    }
}
