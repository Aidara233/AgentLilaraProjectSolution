using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Logging;

namespace Plugin.ScheduledTasks;

[Component(Name = "scheduled-tasks", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Disabled)]
public class ScheduledTasksComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private ScheduledTaskStore? _store;
    private ISignalLogger? _log;
    private string? _pendingNotification;

    private ScheduleTaskTool? _scheduleTool;
    private CancelTaskTool? _cancelTool;
    private ListTasksTool? _listTool;

    private const string LogGroup = "scheduled-tasks";

    public override ComponentMeta Meta => new()
    {
        Name = "scheduled-tasks",
        Description = "定时任务调度：在指定时间触发提醒",
        DefaultEnabled = true,
        PromptPriority = 40
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_scheduleTool != null) yield return _scheduleTool;
            if (_cancelTool != null) yield return _cancelTool;
            if (_listTool != null) yield return _listTool;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;
        _log = context.GetService<ISignalLogger>();
        _store = new ScheduledTaskStore(context.Storage.InstanceDirectory);

        _log?.Event(LogGroup, "init", new { loopId = context.LoopId, reason = reason.ToString() });

        _scheduleTool = new ScheduleTaskTool(_store);
        _cancelTool = new CancelTaskTool(_store);
        _listTool = new ListTasksTool(_store);

        _store.OnTasksChanged = ScheduledTasksNotifier.NotifyChanged;

        RecoverOverdueTasks();
        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        _log?.Event(LogGroup, "shutdown", new { reason = reason.ToString() });
        return Task.CompletedTask;
    }

    public override Task OnBeforeInvokeAsync()
    {
        CheckDueTasksSync();
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        if (_store == null) return null;
        CheckDueTasksSync();
        var text = Interlocked.Exchange(ref _pendingNotification, null);
        if (text != null)
            _log?.Event(LogGroup, "notification-injected", new { length = text.Length });
        return text;
    }

    // ── Due task checking ──

    private void CheckDueTasksSync()
    {
        if (_store == null) return;

        var now = DateTime.Now;
        var (_, dueTasks) = _store.LoadAndFindDue(now);
        if (dueTasks.Count == 0) return;

        _log?.Event(LogGroup, "due-tasks-found", new { count = dueTasks.Count, now = now.ToString("HH:mm:ss") });

        var notifications = new List<string>();

        foreach (var task in dueTasks)
        {
            _log?.Event(LogGroup, "task-firing", new
            {
                id = task.Id[..8],
                description = task.Description,
                isRecurring = task.IsRecurring
            });

            notifications.Add(task.Description);

            if (task.IsRecurring)
            {
                var nextFire = TimeExpressionParser.GetNextRecurrence(task.Expression, task.NextFireTime);
                task.NextFireTime = nextFire;
                task.LastFiredAt = now;
                if (nextFire == null) task.Enabled = false;
            }
            else
            {
                task.NextFireTime = null;
                task.LastFiredAt = now;
                task.Enabled = false;
            }

            _store.UpdateTask(task);
        }

        if (notifications.Count > 0)
        {
            var lines = new List<string> { "[定时任务通知]" };
            foreach (var desc in notifications)
                lines.Add($"- {desc}（已触发）");
            lines.Add("请处理以上定时任务。");
            _pendingNotification = string.Join("\n", lines);
        }
    }

    private void RecoverOverdueTasks()
    {
        if (_store == null) return;

        var now = DateTime.Now;
        var (_, overdue) = _store.LoadAndFindDue(now);
        if (overdue.Count == 0) return;

        _log?.Event(LogGroup, "recovery", new { count = overdue.Count });

        var notifications = new List<string>();

        foreach (var task in overdue)
        {
            notifications.Add(task.Description);

            if (task.IsRecurring)
            {
                var nextFire = TimeExpressionParser.GetNextRecurrence(task.Expression, now.AddMinutes(-1));
                task.NextFireTime = nextFire;
                task.LastFiredAt = now;
                if (nextFire == null) task.Enabled = false;
            }
            else
            {
                task.NextFireTime = null;
                task.LastFiredAt = now;
                task.Enabled = false;
            }

            _store.UpdateTask(task);
        }

        if (notifications.Count > 0)
        {
            var lines = new List<string> { "[定时任务通知]" };
            foreach (var desc in notifications)
                lines.Add($"- {desc}（已触发）");
            lines.Add("请处理以上定时任务。");
            _pendingNotification = string.Join("\n", lines);
        }
    }
}
