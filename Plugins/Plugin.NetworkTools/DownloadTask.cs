// Plugins/Plugin.NetworkTools/DownloadTask.cs
namespace Plugin.NetworkTools;

public enum DownloadStatus { Pending, Downloading, Completed, Failed, Cancelled }

public class DownloadTask
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public string SavePath { get; init; } = "";     // 完整磁盘路径
    public string RelativePath { get; init; } = "";  // Agent 指定的相对路径（通知用）
    public string LoopId { get; init; } = "";
    public DownloadStatus Status { get; set; } = DownloadStatus.Pending;
    public long BytesDownloaded { get; set; }
    public long? TotalBytes { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? FileName { get; set; }            // 从URL或Content-Disposition提取
    public CancellationTokenSource? Cts { get; set; }

    public int ProgressPct => TotalBytes is > 0
        ? (int)(BytesDownloaded * 100 / TotalBytes.Value)
        : -1;

    public string? SpeedString
    {
        get
        {
            if (Status != DownloadStatus.Downloading || BytesDownloaded == 0)
                return null;
            var elapsed = (DateTime.UtcNow - StartedAt).TotalSeconds;
            if (elapsed < 0.5) return null;
            var bps = BytesDownloaded / elapsed;
            return bps switch
            {
                > 1_000_000 => $"{bps / 1_000_000:F1} MB/s",
                > 1_000 => $"{bps / 1_000:F1} KB/s",
                _ => $"{bps:F0} B/s"
            };
        }
    }

    public string SizeString => TotalBytes switch
    {
        > 1_000_000 => $"{TotalBytes / 1_000_000.0:F1} MB",
        > 1_000 => $"{TotalBytes / 1_000.0:F1} KB",
        _ => $"{TotalBytes} B"
    };
}
