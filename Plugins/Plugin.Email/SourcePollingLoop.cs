using AgentLilara.PluginSDK.Sources;

namespace Plugin.Email;

/// <summary>
/// 轮询编排：只管“什么时候再调一次、失败怎么退避、状态记成什么”，不含任何 IMAP 细节，
/// 所以能脱网测试。
/// <para>⚠️ 这里最要紧的一条纪律：**只有取消能退出循环**。一次失败就 return 的话，
/// 信源会永久沉默，而 <c>StartAsync</c> 的幂等判定又会因为“循环对象还在”而拒绝重建——
/// 表面有对象、实际没人干活，这种静默停机比崩溃难查得多。</para>
/// </summary>
public sealed class SourcePollingLoop
{
    private readonly Func<CancellationToken, Task> _poll;
    private readonly int _intervalSeconds;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public SourcePollingLoop(
        Func<CancellationToken, Task> poll,
        int intervalSeconds,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _poll = poll;
        _intervalSeconds = intervalSeconds;
        _delay = delay ?? ((span, ct) => Task.Delay(span, ct));
    }

    public EventSourceState State { get; private set; } = EventSourceState.Stopped;

    public string? LastError { get; private set; }

    public DateTimeOffset? LastErrorAt { get; private set; }

    /// <summary>连续失败次数（成功一次就清零）。</summary>
    public int Failures { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        State = EventSourceState.Running;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _poll(ct);
                State = EventSourceState.Running;
                Failures = 0;
                LastError = null;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Failures++;
                State = EventSourceState.Error;
                LastError = ex.Message;
                LastErrorAt = DateTimeOffset.UtcNow;
            }

            try
            {
                await _delay(NextDelay(), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        State = EventSourceState.Stopped;
    }

    /// <summary>
    /// 下一次等待多久。连续失败按 2 的幂退避（上限 10 分钟）——别用固定间隔去捶一个连不上的服务器，
    /// 那既是无效流量，也可能被对方当成攻击。
    /// </summary>
    public TimeSpan NextDelay()
    {
        var baseSeconds = Math.Max(10, _intervalSeconds);

        if (Failures == 0)
        {
            return TimeSpan.FromSeconds(baseSeconds);
        }

        var factor = Math.Pow(2, Math.Min(Failures, 6));
        return TimeSpan.FromSeconds(Math.Min(600, baseSeconds * factor));
    }
}
