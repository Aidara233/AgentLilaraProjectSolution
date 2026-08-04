using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Logging;

namespace Plugin.ScheduledTasks;

[Component(Name = "scheduled-tasks-timer", Scope = ComponentScope.Global)]
public class ScheduledTasksTimerComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private ISignalLogger? _log;
    private CancellationTokenSource? _shutdownCts;
    private CancellationTokenSource? _delayCts;
    private Task? _timerTask;
    private string _pluginDataBase = "";

    private const string LogGroup = "scheduled-tasks";

    public override ComponentMeta Meta => new()
    {
        Name = "scheduled-tasks-timer",
        Description = "定时任务全局计时器（跨频道）",
        DefaultEnabled = true,
        PromptPriority = 99
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;
        _log = context.GetService<ISignalLogger>();

        // Navigate to the Loop component's storage base:
        // Global storage = PluginData/scheduled-tasks-timer/
        // Loop storage   = PluginData/scheduled-tasks/{sessionId}/
        _pluginDataBase = Path.GetFullPath(Path.Combine(context.Storage.GlobalDirectory, "..", "scheduled-tasks"));

        _log?.Event(LogGroup, "global-timer-init", new { scanPath = _pluginDataBase });

        // Wire up reschedule callback so Loop components can signal us
        ScheduledTasksNotifier.OnReschedule = Reschedule;

        StartTimer();
        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        _log?.Event(LogGroup, "global-timer-shutdown");
        StopTimer();
        _shutdownCts?.Dispose();
        _shutdownCts = null;
        ScheduledTasksNotifier.OnReschedule = null;
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection(LoopInfo caller) => null;

    // ── Timer ──

    private void StartTimer()
    {
        if (_shutdownCts != null) return;
        _shutdownCts = new CancellationTokenSource();
        _timerTask = Task.Run(() => TimerLoop(_shutdownCts.Token));
    }

    private void StopTimer()
    {
        _shutdownCts?.Cancel();
        _delayCts?.Cancel();
        _shutdownCts = null;
        _delayCts = null;
        _timerTask = null;
    }

    private void Reschedule()
    {
        _delayCts?.Cancel();
    }

    private async Task TimerLoop(CancellationToken shutdownCt)
    {
        _log?.Event(LogGroup, "global-timer-loop-started");

        while (!shutdownCt.IsCancellationRequested)
        {
            try
            {
                var (nextFire, sessionId, requiresWork) = FindEarliestTask();

                if (nextFire == null)
                {
                    _log?.Debug(LogGroup, "global-timer-idle");
                    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCt);
                    _delayCts = idleCts;
                    try { await Task.Delay(Timeout.Infinite, idleCts.Token); }
                    catch (OperationCanceledException) when (!shutdownCt.IsCancellationRequested) { }
                    finally { _delayCts = null; }
                    continue;
                }

                var delay = nextFire.Value - DateTime.Now;
                if (delay <= TimeSpan.Zero)
                {
                    _log?.Event(LogGroup, "global-timer-fire", new { sessionId, overdue = -(int)delay.TotalSeconds, requiresWork });
                    FireScheduledTask(sessionId!, requiresWork);
                    try { await Task.Delay(2000, shutdownCt); } catch { break; }
                    continue;
                }

                // Clamp to max supported Timer duration (~49.7 days) to avoid ArgumentOutOfRangeException
                var maxDelay = TimeSpan.FromMilliseconds(0xfffffffe);
                if (delay > maxDelay) delay = maxDelay;

                _log?.Debug(LogGroup, "global-timer-waiting", new
                {
                    sessionId,
                    nextFire = nextFire.Value.ToString("HH:mm:ss"),
                    delaySeconds = (int)delay.TotalSeconds
                });

                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCt);
                _delayCts = delayCts;
                try
                {
                    await Task.Delay(delay, delayCts.Token);
                    _log?.Event(LogGroup, "global-timer-fire", new { sessionId, requiresWork });
                    FireScheduledTask(sessionId!, requiresWork);
                }
                catch (OperationCanceledException) when (!shutdownCt.IsCancellationRequested)
                {
                    _log?.Debug(LogGroup, "global-timer-rescheduled");
                }
                finally { _delayCts = null; }
            }
            catch (Exception ex)
            {
                _log?.Error(LogGroup, "global-timer-error", new { error = ex.Message });
                try { await Task.Delay(5000, shutdownCt); } catch { break; }
            }
        }

        _log?.Event(LogGroup, "global-timer-loop-stopped");
    }

    private (DateTime? nextFire, string? sessionId, bool requiresWork) FindEarliestTask()
    {
        if (!Directory.Exists(_pluginDataBase))
            return (null, null, false);

        DateTime? earliest = null;
        string? earliestSessionId = null;
        bool earliestRequiresWork = false;

        foreach (var dir in Directory.GetDirectories(_pluginDataBase))
        {
            var taskFile = Path.Combine(dir, "scheduled_tasks.json");
            if (!File.Exists(taskFile)) continue;

            var store = new ScheduledTaskStore(dir);
            var (nextFire, requiresWork) = store.GetNextFireTimeWithFlag();
            if (nextFire != null && (earliest == null || nextFire < earliest))
            {
                earliest = nextFire;
                // 目录名即裸 sessionId（ComponentStorage 按裸 sessionId 建实例目录）
                var dirName = Path.GetFileName(dir);
                earliestSessionId = dirName;
                earliestRequiresWork = requiresWork;
            }
        }

        return (earliest, earliestSessionId, earliestRequiresWork);
    }

    private void FireScheduledTask(string sessionId, bool requiresWork)
    {
        _ctx.WakeLoop(sessionId);
    }
}

/// <summary>
/// Static bridge: Loop components signal the Global timer to reschedule.
/// </summary>
internal static class ScheduledTasksNotifier
{
    public static Action? OnReschedule { get; set; }
    public static void NotifyChanged() => OnReschedule?.Invoke();
}
