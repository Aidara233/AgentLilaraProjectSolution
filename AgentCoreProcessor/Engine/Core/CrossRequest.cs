using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine;

public enum CrossRequestState
{
    Submitted,
    Accepted,
    Rejected,
    InProgress,
    Completed,
    Failed,
    Idle,
    Archived,
    Timeout
}

public enum CrossRequestResponseType
{
    Accept,
    Reject,
    Progress,
    Complete,
    Failed,
    Ignore
}

public class CrossRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string InitiatorId { get; set; } = "";
    public string? TargetId { get; set; }
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public CrossRequestState State { get; set; } = CrossRequestState.Submitted;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public List<CrossRequestResponse> Responses { get; set; } = new();
    public string? TraceSignalId { get; set; }
    public string? TraceParentSpanId { get; set; }
}

public class CrossRequestResponse
{
    public int SequenceNumber { get; set; }
    public string ResponderId { get; set; } = "";
    public CrossRequestResponseType Type { get; set; }
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class DelegationNotification
{
    public string RequestId { get; set; } = "";
    public string Title { get; set; } = "";
    public CrossRequestState NewState { get; set; }
    public CrossRequestResponseType ResponseType { get; set; }
    public string? ResponderId { get; set; }
    public string? Content { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
