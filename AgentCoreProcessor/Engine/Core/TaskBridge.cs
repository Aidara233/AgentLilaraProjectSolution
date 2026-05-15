using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 频道循环 ↔ 系统循环异步通信桥梁。
    /// 提供任务队列（重量请求）和通知队列（轻量信号）。
    /// </summary>
    public class TaskBridge
    {
        private readonly Channel<SystemTask> taskQueue;
        private readonly Channel<Notification> notificationQueue;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<TaskResult>> pendingTasks;
        private readonly string persistencePath;
        private readonly object persistenceLock = new();

        public ChannelReader<SystemTask> TaskReader => taskQueue.Reader;
        public ChannelReader<Notification> NotificationReader => notificationQueue.Reader;
        public int PendingTaskCount => pendingTasks.Count;

        /// <summary>任务队列中是否有待处理项。</summary>
        public bool HasPendingTasks() => taskQueue.Reader.TryPeek(out _);

        /// <summary>通知队列中是否有待处理项。</summary>
        public bool HasPendingNotifications() => notificationQueue.Reader.TryPeek(out _);

        /// <summary>任务提交后的回调（用于唤醒系统循环闸门）。</summary>
        public Action? OnTaskSubmitted { get; set; }

        /// <summary>系统循环当前状态（供频道循环感知起床气等）。</summary>
        public SystemLoopState SystemState { get; set; } = SystemLoopState.Active;

        public TaskBridge(string storagePath)
        {
            taskQueue = Channel.CreateUnbounded<SystemTask>();
            notificationQueue = Channel.CreateUnbounded<Notification>();
            pendingTasks = new ConcurrentDictionary<string, TaskCompletionSource<TaskResult>>();
            persistencePath = Path.Combine(storagePath, "task_queue.json");

            // 重启恢复
            LoadPendingTasks();
        }

        /// <summary>
        /// 提交任务给系统循环，等待结果。
        /// </summary>
        public async Task<TaskResult> SubmitTaskAsync(SystemTask task, TimeSpan timeout)
        {
            task.TraceSignalId ??= Logging.SignalContext.Current?.SignalId;
            task.TraceParentSpanId ??= Logging.SignalContext.Current?.CurrentSpanId;

            var tcs = new TaskCompletionSource<TaskResult>();
            if (!pendingTasks.TryAdd(task.TaskId, tcs))
            {
                throw new InvalidOperationException($"任务 ID 冲突: {task.TaskId}");
            }

            // 入队并持久化
            await taskQueue.Writer.WriteAsync(task);
            PersistQueue();
            OnTaskSubmitted?.Invoke();


            // 等待结果或超时
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                var resultTask = tcs.Task;
                var completedTask = await Task.WhenAny(resultTask, Task.Delay(timeout, cts.Token));
                if (completedTask == resultTask)
                {
                    return await resultTask;
                }
                else
                {
                    pendingTasks.TryRemove(task.TaskId, out _);
                    return new TaskResult
                    {
                        TaskId = task.TaskId,
                        Success = false,
                        Error = "任务超时"
                    };
                }
            }
            catch (Exception ex)
            {
                pendingTasks.TryRemove(task.TaskId, out _);
                return new TaskResult
                {
                    TaskId = task.TaskId,
                    Success = false,
                    Error = $"任务异常: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 异步提交任务（不等待结果）。系统循环处理完后通过其他方式回传。
        /// </summary>
        public async Task SubmitTaskFireAndForgetAsync(SystemTask task)
        {
            task.TraceSignalId ??= Logging.SignalContext.Current?.SignalId;
            task.TraceParentSpanId ??= Logging.SignalContext.Current?.CurrentSpanId;
            await taskQueue.Writer.WriteAsync(task);
            PersistQueue();
            OnTaskSubmitted?.Invoke();
        }

        /// <summary>
        /// 完成任务，返回结果给提交者。
        /// </summary>
        public void CompleteTask(string taskId, TaskResult result)
        {
            if (pendingTasks.TryRemove(taskId, out var tcs))
            {
                tcs.SetResult(result);
                PersistQueue();
            }
            else
            {
            }
        }

        /// <summary>
        /// 发送轻量通知（不打断系统循环）。
        /// </summary>
        public void PostNotification(Notification notification)
        {
            notification.TraceSignalId ??= Logging.SignalContext.Current?.SignalId;
            notification.TraceParentSpanId ??= Logging.SignalContext.Current?.CurrentSpanId;
            notificationQueue.Writer.TryWrite(notification);
        }

        /// <summary>
        /// 读取通知（系统循环按需拉取）。
        /// </summary>
        public async Task<List<Notification>> ReadNotificationsAsync(int count, TimeSpan timeout)
        {
            var notifications = new List<Notification>();
            using var cts = new CancellationTokenSource(timeout);

            try
            {
                for (int i = 0; i < count; i++)
                {
                    if (await notificationQueue.Reader.WaitToReadAsync(cts.Token))
                    {
                        if (notificationQueue.Reader.TryRead(out var notification))
                        {
                            notifications.Add(notification);
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 超时，返回已读取的
            }

            return notifications;
        }

        private void PersistQueue()
        {
            lock (persistenceLock)
            {
                try
                {
                    var data = new
                    {
                        PendingTaskIds = pendingTasks.Keys.ToList(),
                        Timestamp = DateTime.Now
                    };
                    var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(persistencePath, json);
                }
                catch (Exception ex)
                {
                }
            }
        }

        private void LoadPendingTasks()
        {
            if (!File.Exists(persistencePath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(persistencePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                // 注意：重启后任务 ID 会被加载，但实际任务对象已丢失
                // 系统循环重启时会清理这些孤儿任务
            }
            catch (Exception ex)
            {
            }
        }
    }

    /// <summary>
    /// 系统任务（重量请求）。
    /// </summary>
    public class SystemTask
    {
        public string TaskId { get; set; } = Guid.NewGuid().ToString();
        public int SourceChannelId { get; set; }
        public string Description { get; set; } = "";
        public string? ContextSummary { get; set; }
        public int RequestingPersonId { get; set; }
        public int Priority { get; set; }
        public DateTime SubmittedAt { get; set; } = DateTime.Now;
        public string? TraceSignalId { get; set; }
        public string? TraceParentSpanId { get; set; }
    }

    /// <summary>
    /// 任务结果。
    /// </summary>
    public class TaskResult
    {
        public string TaskId { get; set; } = "";
        public bool Success { get; set; }
        public string? Result { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// 轻量通知（不打断系统循环）。
    /// </summary>
    public class Notification
    {
        public NotificationType Type { get; set; }
        public string SourceId { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? DelegationId { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string? TraceSignalId { get; set; }
        public string? TraceParentSpanId { get; set; }
    }

    public enum NotificationType
    {
        Notify,          // 一般通知
        ProgressUpdate,  // 进度汇报
        WatchHit,        // 关注列表命中
        SubAgentFailed   // 子 agent 执行失败，需系统循环决策
    }
}
