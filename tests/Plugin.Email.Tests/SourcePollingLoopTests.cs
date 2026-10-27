using AgentLilara.PluginSDK.Sources;
using Plugin.Email;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// F1：轮询编排。核心是一条纪律——**只有取消能退出循环**。
/// <para>此前的实现首轮取基线失败就 return：循环死了，但 <c>StartAsync</c> 看到“任务对象还在”
/// 就拒绝重建，于是信源永久沉默，而外部看起来一切正常。这是最坏的一类故障：不报错、不崩、就是不工作。</para>
/// </summary>
public class SourcePollingLoopTests
{
    [Fact]
    public async Task KeepsRetryingAfterFirstFailure()
    {
        var cts = new CancellationTokenSource();
        var attempts = 0;

        var loop = new SourcePollingLoop(
            _ =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("首轮连不上");
                }

                cts.Cancel(); // 第二轮成功后收工
                return Task.CompletedTask;
            },
            intervalSeconds: 60,
            delay: (_, _) => Task.CompletedTask);

        await loop.RunAsync(cts.Token);

        Assert.Equal(2, attempts);                       // 没有因为首轮失败就退出
        Assert.Equal(EventSourceState.Stopped, loop.State);
        Assert.Null(loop.LastError);                     // 成功后错误被清掉
    }

    [Fact]
    public async Task FailureCountResetsAfterSuccess()
    {
        var cts = new CancellationTokenSource();
        var attempts = 0;

        var loop = new SourcePollingLoop(
            _ =>
            {
                attempts++;
                if (attempts <= 3)
                {
                    throw new InvalidOperationException("还连不上");
                }

                cts.Cancel();
                return Task.CompletedTask;
            },
            intervalSeconds: 60,
            delay: (_, _) => Task.CompletedTask);

        await loop.RunAsync(cts.Token);

        Assert.Equal(4, attempts);
        Assert.Equal(0, loop.Failures);
    }

    [Fact]
    public async Task ErrorStateIsVisibleWhileFailing()
    {
        var cts = new CancellationTokenSource();
        SourcePollingLoop? loop = null;
        var observedStates = new List<EventSourceState>();

        loop = new SourcePollingLoop(
            _ => throw new InvalidOperationException("一直连不上"),
            intervalSeconds: 60,
            delay: (_, _) =>
            {
                // 循环内的状态才是"当下真的坏了"；退出后一律 Stopped
                observedStates.Add(loop!.State);
                if (observedStates.Count >= 2)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            });

        await loop.RunAsync(cts.Token);

        Assert.Contains(EventSourceState.Error, observedStates);
        Assert.Equal(EventSourceState.Stopped, loop.State);
        Assert.Equal("一直连不上", loop.LastError);
        Assert.True(loop.Failures >= 2);
    }

    [Fact]
    public async Task BackoffGrowsAndIsCapped()
    {
        var cts = new CancellationTokenSource();
        var delays = new List<TimeSpan>();

        var loop = new SourcePollingLoop(
            _ => throw new InvalidOperationException("x"),
            intervalSeconds: 60,
            delay: (span, _) =>
            {
                delays.Add(span);
                if (delays.Count >= 8)
                {
                    cts.Cancel();
                }

                return Task.CompletedTask;
            });

        await loop.RunAsync(cts.Token);

        Assert.True(delays.Count >= 2);
        Assert.True(delays[1] > delays[0], "连续失败必须退避，不能用固定间隔捶服务器");
        Assert.All(delays, span => Assert.True(span <= TimeSpan.FromSeconds(600)));
    }

    [Fact]
    public void SteadyStateUsesConfiguredInterval()
    {
        var loop = new SourcePollingLoop(_ => Task.CompletedTask, intervalSeconds: 45);

        Assert.Equal(TimeSpan.FromSeconds(45), loop.NextDelay());
    }

    [Fact]
    public void IntervalHasFloor()
    {
        // 配 1 秒轮询会把邮箱当压力测试；下限兜住
        var loop = new SourcePollingLoop(_ => Task.CompletedTask, intervalSeconds: 1);

        Assert.Equal(TimeSpan.FromSeconds(10), loop.NextDelay());
    }
}
