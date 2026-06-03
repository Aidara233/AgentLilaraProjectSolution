// Plugins/Plugin.GroupFileTools/GroupFileDownloadStore.cs
using System.Collections.Concurrent;

namespace Plugin.GroupFileTools;

/// <summary>
/// 全局下载注册表（单例）。下载任务跟踪 + 通知队列。
/// 通过 GroupFileNotifier 静态桥接实现后台任务→Loop 唤醒通信。
/// </summary>
public class GroupFileDownloadStore
{
    private readonly ConcurrentDictionary<string, GroupFileDownloadTask> _tasks = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<GroupFileDownloadNotification>> _notifications = new();

    public GroupFileDownloadTask Register(string loopId, string fileName, string savePath)
    {
        var task = new GroupFileDownloadTask
        {
            Id = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Interlocked.Increment(ref _seq):x4}",
            LoopId = loopId,
            FileName = fileName,
            SavePath = savePath,
            Status = "downloading",
            StartedAt = DateTime.UtcNow
        };
        _tasks[task.Id] = task;
        return task;
    }

    public void MarkCompleted(string taskId, long size)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return;
        if (task.Status != "downloading") return;
        task.Status = "completed";
        task.Size = size;
        task.CompletedAt = DateTime.UtcNow;
        EnqueueNotification(task, "completed", null);
        GroupFileNotifier.NotifyCompleted(task.LoopId);
    }

    public void MarkFailed(string taskId, string error)
    {
        if (!_tasks.TryGetValue(taskId, out var task)) return;
        if (task.Status != "downloading") return;
        task.Status = "failed";
        task.Error = error;
        task.CompletedAt = DateTime.UtcNow;
        // 删除部分下载的文件
        try { if (File.Exists(task.SavePath)) File.Delete(task.SavePath); } catch { }
        EnqueueNotification(task, "failed", error);
        GroupFileNotifier.NotifyCompleted(task.LoopId);
    }

    public List<GroupFileDownloadNotification> DrainNotifications(string loopId)
    {
        if (!_notifications.TryGetValue(loopId, out var queue))
            return new List<GroupFileDownloadNotification>();

        var result = new List<GroupFileDownloadNotification>();
        while (queue.TryDequeue(out var n))
            result.Add(n);
        return result;
    }

    private void EnqueueNotification(GroupFileDownloadTask task, string status, string? error)
    {
        var queue = _notifications.GetOrAdd(task.LoopId, _ => new ConcurrentQueue<GroupFileDownloadNotification>());
        queue.Enqueue(new GroupFileDownloadNotification
        {
            TaskId = task.Id,
            LoopId = task.LoopId,
            FileName = task.FileName,
            SavePath = task.SavePath,
            Size = task.Size,
            Status = status,
            Error = error
        });
    }

    private static int _seq;
}

public class GroupFileDownloadTask
{
    public string Id { get; init; } = "";
    public string LoopId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string SavePath { get; set; } = "";
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public long Size { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class GroupFileDownloadNotification
{
    public string TaskId { get; init; } = "";
    public string LoopId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string SavePath { get; init; } = "";
    public long Size { get; init; }
    public string Status { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>
/// 静态桥接：后台下载完成 → 唤醒 Loop。
/// Global 组件设置 OnDownloadCompleted 回调。
/// </summary>
internal static class GroupFileNotifier
{
    public static Action<string>? OnDownloadCompleted { get; set; }

    public static void NotifyCompleted(string loopId)
        => OnDownloadCompleted?.Invoke(loopId);
}

/// <summary>
/// 静态访问器：Global 组件初始化后设置，Loop 组件工具通过此访问共享资源。
/// </summary>
public static class GroupFileToolsAccessor
{
    public static HttpClient? HttpClient { get; private set; }
    public static GroupFileDownloadStore? Store { get; private set; }

    public static void Configure(HttpClient http, GroupFileDownloadStore store)
    {
        HttpClient = http;
        Store = store;
    }

    public static void Clear()
    {
        HttpClient = null;
        Store = null;
    }
}
