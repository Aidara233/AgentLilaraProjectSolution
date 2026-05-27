// Plugins/Plugin.SshTools/SshTask.cs
namespace Plugin.SshTools;

public enum SshTaskStatus { Running, Completed, Failed, Killed, TimedOut }

public class SshTask
{
    public string TaskId { get; init; } = "";
    public string LoopId { get; init; } = "";
    public string Command { get; init; } = "";
    public SshTaskStatus Status { get; set; } = SshTaskStatus.Running;
    /// <summary>异步任务输出文件（远端路径），同步完成时为 null</summary>
    public string? StdoutFile { get; set; }
    public string? StderrFile { get; set; }
    /// <summary>同步完成时的内联输出（截断），异步完成时为 null</summary>
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public CancellationTokenSource? Cts { get; set; }
}
