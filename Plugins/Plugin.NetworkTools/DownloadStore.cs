// Plugins/Plugin.NetworkTools/DownloadStore.cs
using System.Collections.Concurrent;

namespace Plugin.NetworkTools;

/// <summary>
/// 全局下载注册表（单例）。Global组件管理下载任务，Loop组件读取通知。
/// 通过 NetworkToolsNotifier 静态桥接实现 Global→Loop 唤醒通信。
/// </summary>
public class DownloadStore
{
    private readonly ConcurrentDictionary<string, DownloadTask> _tasks = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DownloadNotification>> _notifications = new();
    private int _activeCount;

    public int MaxConcurrent { get; set; } = 3;

    // ── Task management (called by Global component or tools) ──

    public bool TryAdd(DownloadTask task)
    {
        lock (_tasks)
        {
            if (_activeCount >= MaxConcurrent)
                return false;
            _activeCount++;
        }
        task.Status = DownloadStatus.Downloading;
        task.StartedAt = DateTime.UtcNow;
        return _tasks.TryAdd(task.Id, task);
    }

    public DownloadTask? Get(string id)
    {
        _tasks.TryGetValue(id, out var task);
        return task;
    }

    public List<DownloadTask> GetAll(string? filter, string? loopId)
    {
        var query = _tasks.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(loopId))
            query = query.Where(t => t.LoopId == loopId);

        query = filter switch
        {
            "active" => query.Where(t => t.Status == DownloadStatus.Downloading || t.Status == DownloadStatus.Pending),
            "completed" => query.Where(t => t.Status == DownloadStatus.Completed),
            "failed" => query.Where(t => t.Status == DownloadStatus.Failed || t.Status == DownloadStatus.Cancelled),
            _ => query
        };

        return query.OrderByDescending(t => t.StartedAt).ToList();
    }

    public bool Cancel(string id)
    {
        if (!_tasks.TryGetValue(id, out var task))
            return false;

        if (task.Status is DownloadStatus.Completed or DownloadStatus.Cancelled)
            return false;

        task.Cts?.Cancel();
        task.Status = DownloadStatus.Cancelled;
        task.CompletedAt = DateTime.UtcNow;
        DecrementActive();

        EnqueueNotification(task, "cancelled", error: null);
        NetworkToolsNotifier.NotifyCompleted(task.LoopId);
        return true;
    }

    public void MarkCompleted(DownloadTask task, long totalBytes)
    {
        task.Status = DownloadStatus.Completed;
        task.BytesDownloaded = totalBytes;
        task.CompletedAt = DateTime.UtcNow;
        DecrementActive();

        EnqueueNotification(task, "completed", error: null);
        NetworkToolsNotifier.NotifyCompleted(task.LoopId);
    }

    public void MarkFailed(DownloadTask task, string error)
    {
        task.Status = DownloadStatus.Failed;
        task.Error = error;
        task.CompletedAt = DateTime.UtcNow;
        DecrementActive();

        // 删除部分下载的文件
        try { if (File.Exists(task.SavePath)) File.Delete(task.SavePath); }
        catch { /* best effort */ }

        EnqueueNotification(task, "failed", error: error);
        NetworkToolsNotifier.NotifyCompleted(task.LoopId);
    }

    // ── Notification drain (called by Loop component) ──

    public List<DownloadNotification> DrainNotifications(string loopId)
    {
        if (!_notifications.TryGetValue(loopId, out var queue))
            return new List<DownloadNotification>();

        var result = new List<DownloadNotification>();
        while (queue.TryDequeue(out var n))
            result.Add(n);
        return result;
    }

    public void Shutdown()
    {
        foreach (var task in _tasks.Values)
            task.Cts?.Cancel();
        _tasks.Clear();
        _activeCount = 0;
    }

    private void EnqueueNotification(DownloadTask task, string status, string? error)
    {
        var queue = _notifications.GetOrAdd(task.LoopId, _ => new ConcurrentQueue<DownloadNotification>());
        queue.Enqueue(new DownloadNotification
        {
            DownloadId = task.Id,
            LoopId = task.LoopId,
            FileName = task.FileName ?? Path.GetFileName(task.SavePath),
            RelativePath = task.RelativePath,
            Size = task.BytesDownloaded,
            Status = status,
            Error = error
        });
    }

    private void DecrementActive()
    {
        lock (_tasks) { _activeCount--; }
    }
}

public class DownloadNotification
{
    public string DownloadId { get; init; } = "";
    public string LoopId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public long Size { get; init; }
    public string Status { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>
/// 静态桥接：Loop组件→Global组件唤醒信号。
/// </summary>
public static class NetworkToolsNotifier
{
    /// <summary>Global组件设置此回调：收到通知时调用WakeLoop。</summary>
    public static Action<string>? OnDownloadCompleted { get; set; }

    public static void NotifyCompleted(string loopId)
        => OnDownloadCompleted?.Invoke(loopId);
}
