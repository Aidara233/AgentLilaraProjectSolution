using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine.Source;

/// <summary>心跳调度与离线阈值判断；宿主提供事件发布、存活查询和告警投递。</summary>
internal sealed class HeartbeatService
{
    private readonly Action _publishTick;
    private readonly Func<bool> _isSessionAlive;
    private readonly Func<Task> _sendAlarm;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private DateTime _lastHeartbeat;
    private bool _alarmSent;
    private int _intervalSeconds = 240;

    public HeartbeatService(Action publishTick, Func<bool> isSessionAlive, Func<Task> sendAlarm,
        Func<DateTime>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _publishTick = publishTick;
        _isSessionAlive = isSessionAlive;
        _sendAlarm = sendAlarm;
        _now = now ?? (() => DateTime.Now);
        _delay = delay ?? Task.Delay;
        _lastHeartbeat = _now();
    }

    public int IntervalSeconds => Volatile.Read(ref _intervalSeconds);

    public void SetInterval(int seconds)
    {
        if (seconds > 0) Volatile.Write(ref _intervalSeconds, seconds);
    }

    public async Task RunAsync(CancellationToken ct, Func<bool> isAlive)
    {
        while (isAlive() && !ct.IsCancellationRequested)
        {
            try { await _delay(TimeSpan.FromSeconds(IntervalSeconds), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            if (!isAlive() || ct.IsCancellationRequested) break;

            using var tickSignal = Signal.Begin(LogGroup.Engine, "timer:heartbeat", "心跳");
            _publishTick();
            if (_isSessionAlive())
            {
                _lastHeartbeat = _now();
                _alarmSent = false;
            }
            else if ((_now() - _lastHeartbeat).TotalHours > 1.0 && !_alarmSent)
            {
                await _sendAlarm();
                _alarmSent = true;
            }
        }
    }
}
