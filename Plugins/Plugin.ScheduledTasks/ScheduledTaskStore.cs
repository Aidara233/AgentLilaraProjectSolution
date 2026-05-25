using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Plugin.ScheduledTasks;

public class ScheduledTaskEntry
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string Expression { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? NextFireTime { get; set; }
    public DateTime? LastFiredAt { get; set; }
    public bool IsRecurring { get; set; }
    public bool Enabled { get; set; } = true;
}

public class PendingNotification
{
    public string TaskId { get; set; } = "";
    public string Description { get; set; } = "";
}

public class ScheduledTaskStore
{
    private readonly string _filePath;
    private readonly List<PendingNotification> _pendingNotifications = new();
    private readonly object _lock = new();

    public ScheduledTaskStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "scheduled_tasks.json");
    }

    public List<ScheduledTaskEntry> LoadTasks()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new List<ScheduledTaskEntry>();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<ScheduledTaskEntry>>(json)
                   ?? new List<ScheduledTaskEntry>();
        }
        catch
        {
            return new List<ScheduledTaskEntry>();
        }
    }

    public void SaveTasks(List<ScheduledTaskEntry> tasks)
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);
        var tmp = _filePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(tasks, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _filePath, overwrite: true);
    }

    public ScheduledTaskEntry AddTask(string description, string expression, DateTime? nextFire, bool isRecurring)
    {
        lock (_lock)
        {
            var tasks = LoadTasks();
            var entry = new ScheduledTaskEntry
            {
                Id = Guid.NewGuid().ToString(),
                Description = description,
                Expression = expression,
                CreatedAt = DateTime.Now,
                NextFireTime = nextFire,
                IsRecurring = isRecurring,
                Enabled = true
            };
            tasks.Add(entry);
            SaveTasks(tasks);
            return entry;
        }
    }

    public bool RemoveTask(string taskId)
    {
        lock (_lock)
        {
            var tasks = LoadTasks();
            var exact = tasks.FirstOrDefault(t => t.Id == taskId);
            if (exact != null)
            {
                tasks.Remove(exact);
                SaveTasks(tasks);
                return true;
            }
            var matches = tasks.Where(t => t.Id.StartsWith(taskId)).ToList();
            if (matches.Count == 1)
            {
                tasks.Remove(matches[0]);
                SaveTasks(tasks);
                return true;
            }
            return false;
        }
    }

    public List<ScheduledTaskEntry> GetActiveTasks()
    {
        lock (_lock)
        {
            return LoadTasks().Where(t => t.Enabled).ToList();
        }
    }

    public ScheduledTaskEntry? GetTask(string taskId)
    {
        lock (_lock)
        {
            return LoadTasks().FirstOrDefault(t => t.Id == taskId || t.Id.StartsWith(taskId));
        }
    }

    public void UpdateTask(ScheduledTaskEntry updated)
    {
        lock (_lock)
        {
            var tasks = LoadTasks();
            var idx = tasks.FindIndex(t => t.Id == updated.Id);
            if (idx >= 0)
            {
                tasks[idx] = updated;
                SaveTasks(tasks);
            }
        }
    }

    public (List<ScheduledTaskEntry> tasks, List<ScheduledTaskEntry> dueTasks) LoadAndFindDue(DateTime now)
    {
        lock (_lock)
        {
            var tasks = LoadTasks();
            var due = tasks.Where(t => t.Enabled && t.NextFireTime.HasValue && t.NextFireTime.Value <= now).ToList();
            return (tasks, due);
        }
    }

    public void EnqueueNotification(string taskId, string description)
    {
        lock (_pendingNotifications)
        {
            _pendingNotifications.Add(new PendingNotification { TaskId = taskId, Description = description });
        }
    }

    public List<PendingNotification> DrainNotifications()
    {
        lock (_pendingNotifications)
        {
            var result = new List<PendingNotification>(_pendingNotifications);
            _pendingNotifications.Clear();
            return result;
        }
    }

    public bool HasPendingNotifications()
    {
        lock (_pendingNotifications)
        {
            return _pendingNotifications.Count > 0;
        }
    }
}
