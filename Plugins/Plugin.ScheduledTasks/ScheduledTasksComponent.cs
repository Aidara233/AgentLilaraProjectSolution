using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.ScheduledTasks;

[Component(Name = "scheduled-tasks", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Disabled)]
[ToolVisibility(Default = Visibility.AlwaysVisible)]
public class ScheduledTasksComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private ScheduledTaskStore? _store;
    private CancellationTokenSource? _timerCts;
    private Task? _timerTask;

    private ScheduleTaskTool? _scheduleTool;
    private CancelTaskTool? _cancelTool;
    private ListTasksTool? _listTool;

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
        _store = new ScheduledTaskStore(context.Storage.InstanceDirectory);

        _scheduleTool = new ScheduleTaskTool(_store);
        _cancelTool = new CancelTaskTool(_store);
        _listTool = new ListTasksTool(_store);

        // Cold-start recovery: fire tasks that became due while inactive
        if (reason == InitReason.Fresh || reason == InitReason.Reload)
            RecoverOverdueTasks();

        return Task.CompletedTask;
    }

    public override Task OnEnabledAsync()
    {
        StartTimer();
        RecoverOverdueTasks();
        return Task.CompletedTask;
    }

    public override Task OnDisabledAsync()
    {
        StopTimer();
        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        StopTimer();
        _timerCts?.Dispose();
        _timerCts = null;
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        if (_store == null) return null;

        var notifications = _store.DrainNotifications();
        if (notifications.Count == 0) return null;

        var lines = new List<string> { "[定时任务通知]" };
        foreach (var n in notifications)
            lines.Add($"- {n.Description}（已触发）");
        lines.Add("请处理以上定时任务。");

        return string.Join("\n", lines);
    }

    // ── Background Timer ──

    private void StartTimer()
    {
        if (_timerCts != null) return; // already running
        _timerCts = new CancellationTokenSource();
        _timerTask = Task.Run(() => PollLoop(_timerCts.Token));
    }

    private void StopTimer()
    {
        _timerCts?.Cancel();
        _timerCts = null;
        _timerTask = null;
    }

    private async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                await CheckDueTasks();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Swallow - don't crash the polling loop on transient errors
            }
        }
    }

    // ── Task Checking ──

    private Task CheckDueTasks()
    {
        if (_store == null) return Task.CompletedTask;

        var (tasks, dueTasks) = _store.LoadAndFindDue(DateTime.Now);
        if (dueTasks.Count == 0) return Task.CompletedTask;

        var now = DateTime.Now;
        var fired = false;

        foreach (var task in dueTasks)
        {
            _store.EnqueueNotification(task.Id, task.Description);

            if (task.IsRecurring)
            {
                var nextFire = TimeExpressionParser.GetNextRecurrence(task.Expression, task.NextFireTime);
                if (nextFire != null)
                {
                    task.NextFireTime = nextFire;
                    task.LastFiredAt = now;
                }
                else
                {
                    // Can't compute next recurrence - disable
                    task.Enabled = false;
                    task.NextFireTime = null;
                    task.LastFiredAt = now;
                }
            }
            else
            {
                task.NextFireTime = null;
                task.LastFiredAt = now;
                task.Enabled = false;
            }

            _store.UpdateTask(task);
            fired = true;
        }

        if (fired)
            _ctx.WakeLoop();

        return Task.CompletedTask;
    }

    private void RecoverOverdueTasks()
    {
        if (_store == null) return;

        var (tasks, overdue) = _store.LoadAndFindDue(DateTime.Now);
        if (overdue.Count == 0) return;

        var now = DateTime.Now;
        var fired = false;

        foreach (var task in overdue)
        {
            _store.EnqueueNotification(task.Id, task.Description);

            if (task.IsRecurring)
            {
                // For recurring tasks, compute next fire from NOW (not the missed time),
                // to avoid a cascade of missed fires
                var nextFire = TimeExpressionParser.GetNextRecurrence(task.Expression, now.AddMinutes(-1));
                if (nextFire != null)
                {
                    task.NextFireTime = nextFire;
                    task.LastFiredAt = now;
                }
                else
                {
                    task.Enabled = false;
                    task.NextFireTime = null;
                    task.LastFiredAt = now;
                }
            }
            else
            {
                task.NextFireTime = null;
                task.LastFiredAt = now;
                task.Enabled = false;
            }

            _store.UpdateTask(task);
            fired = true;
        }

        if (fired)
            _ctx.WakeLoop();
    }
}
