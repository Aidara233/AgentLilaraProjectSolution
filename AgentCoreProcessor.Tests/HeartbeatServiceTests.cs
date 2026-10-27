using AgentCoreProcessor.Engine.Source;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class HeartbeatServiceTests
{
    [Fact]
    public async Task OfflineAlarmIsStrictlyAfterOneHourAndResetsOnRecovery()
    {
        var start = new DateTime(2026, 1, 1);
        var now = start;
        var times = new[] { 60d, 61d, 90d, 91d, 151d, 152d };
        var ticks = 0;
        var alarms = 0;
        var service = new HeartbeatService(() => ticks++, () => ticks == 4,
            () => { alarms++; return Task.CompletedTask; }, () => now,
            (_, _) => { now = start.AddMinutes(times[ticks]); return Task.CompletedTask; });
        await service.RunAsync(default, () => ticks < times.Length);
        Assert.Equal(6, ticks);
        Assert.Equal(2, alarms);
    }

    [Fact]
    public async Task IntervalUpdatesApplyToNextDelayAndInvalidValuesAreIgnored()
    {
        var delays = new List<TimeSpan>();
        var ticks = 0;
        HeartbeatService? service = null;
        service = new HeartbeatService(() => { ticks++; service!.SetInterval(ticks == 1 ? 7 : -1); },
            () => true, () => Task.CompletedTask,
            delay: (duration, _) => { delays.Add(duration); return Task.CompletedTask; });
        Assert.Equal(240, service.IntervalSeconds);
        await service.RunAsync(default, () => ticks < 3);
        Assert.Equal(new[] { TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7) }, delays);
    }

    [Fact]
    public async Task CancellationDuringDelayDoesNotPublishTick()
    {
        using var stop = new CancellationTokenSource();
        var ticks = 0;
        var service = new HeartbeatService(() => ticks++, () => true, () => Task.CompletedTask,
            delay: (_, ct) => { stop.Cancel(); return Task.FromCanceled(ct); });
        await service.RunAsync(stop.Token, () => true);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public async Task StoppingDuringDelayDoesNotPublishTick()
    {
        var alive = true;
        var ticks = 0;
        var service = new HeartbeatService(() => ticks++, () => true, () => Task.CompletedTask,
            delay: (_, _) => { alive = false; return Task.CompletedTask; });
        await service.RunAsync(default, () => alive);
        Assert.Equal(0, ticks);
    }
}
