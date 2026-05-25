using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.ScheduledTasks;

[Component(Name = "scheduled-tasks", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Disabled)]
public class ScheduledTasksComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private ScheduledTaskStore? _store;
    private CancellationTokenSource? _timerCts;
    private Task? _timerTask;

    // Simple pending notification — no queue/lock complexity.
    // Set by CheckDueTasks, consumed by BuildPromptSection.
    private string? _pendingNotification;

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

        StartTimer();
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

    public override Task OnBeforeInvokeAsync()
    {
        return CheckDueTasks();
    }

    public override string? BuildPromptSection()
    {
        if (_store == null) return null;

        // Check due tasks synchronously in case OnBeforeInvokeAsync missed them
        CheckDueTasksSync();

        var text = Interlocked.Exchange(ref _pendingNotification, null);
        return text;
    }

    // ── Timer ──

    private void StartTimer()
    {
        if (_timerCts != null) return;
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
                // Swallow transient errors
            }
        }
    }

    // ── Due task checking ──

    private Task CheckDueTasks()
    {
        CheckDueTasksSync();
        return Task.CompletedTask;
    }

    private void CheckDueTasksSync()
    {
        if (_store == null) return;

        var now = DateTime.Now;
        var (tasks, dueTasks) = _store.LoadAndFindDue(now);
        if (dueTasks.Count == 0) return;

        var notifications = new List<string>();
        var fired = false;

        foreach (var task in dueTasks)
        {
            notifications.Add(task.Description);

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

        if (notifications.Count > 0)
        {
            var lines = new List<string> { "[定时任务通知]" };
            foreach (var desc in notifications)
                lines.Add($"- {desc}（已触发）");
            lines.Add("请处理以上定时任务。");

            _pendingNotification = string.Join("\n", lines);
        }

        if (fired)
            _ctx.WakeLoop();
    }

    private void RecoverOverdueTasks()
    {
        if (_store == null) return;

        var now = DateTime.Now;
        var (tasks, overdue) = _store.LoadAndFindDue(now);
        if (overdue.Count == 0) return;

        var notifications = new List<string>();
        var fired = false;

        foreach (var task in overdue)
        {
            notifications.Add(task.Description);

            if (task.IsRecurring)
            {
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

        if (notifications.Count > 0)
        {
            var lines = new List<string> { "[定时任务通知]" };
            foreach (var desc in notifications)
                lines.Add($"- {desc}（已触发）");
            lines.Add("请处理以上定时任务。");

            _pendingNotification = string.Join("\n", lines);
        }

        if (fired)
            _ctx.WakeLoop();
    }
}
