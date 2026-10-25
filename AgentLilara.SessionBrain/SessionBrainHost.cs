namespace AgentLilara.SessionBrain;

/// <summary>
/// 宿主侧的接缝运行器：只负责「按模式决定要不要旁路」，不实现任何转换。
///
/// <para>⚠️ 影子调用是 <b>fire-and-forget</b> 的：它绝不能拖慢或打断会话。
/// 因此这里的每一条路径都吞掉异常并交给 <see cref="OnFailure"/>。</para>
/// </summary>
public sealed class SessionBrainHost : IDisposable
{
    private static readonly TimeSpan ShadowTimeout = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _shadowSlots;

    public static SessionBrainHost Off { get; } =
        new(SessionBrainMode.Off, kernel: null, onFailure: null, maxConcurrentShadows: 0);

    public SessionBrainMode Mode { get; }
    public ISessionBrain? Kernel { get; }

    /// <summary>影子失败的报告出口（参数：一句话 + 异常）。默认丢弃。</summary>
    public Action<string, Exception?>? OnFailure { get; }

    public SessionBrainHost(
        SessionBrainMode mode,
        ISessionBrain? kernel,
        Action<string, Exception?>? onFailure = null,
        int maxConcurrentShadows = 2)
    {
        if (mode != SessionBrainMode.Off && kernel is null)
            throw new ArgumentException("启用接缝时必须提供内核实现。", nameof(kernel));

        Mode = mode;
        Kernel = kernel;
        OnFailure = onFailure;
        _shadowSlots = new SemaphoreSlim(Math.Max(1, maxConcurrentShadows));
    }

    public bool IsEnabled => Mode != SessionBrainMode.Off;

    public void Dispose() => _shadowSlots.Dispose();

    /// <summary>
    /// 用一次**已经发生的** Legacy 调用构造出的请求，旁路跑一次内核。
    /// ⚠️ 不等待、不抛异常；内核自己负责「不写 ALP 历史、不执行有副作用的工具」。
    /// </summary>
    public void RunShadow(SessionBrainRequest request, CancellationToken cancellationToken = default)
    {
        if (Mode != SessionBrainMode.Shadow || Kernel is null) return;

        _ = Task.Run(async () =>
        {
            if (!await _shadowSlots.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
            {
                OnFailure?.Invoke("影子调用被跳过：并发槽已满。", null);
                return;
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ShadowTimeout);
                await Kernel.InvokeAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnFailure?.Invoke("影子调用失败。", ex);
            }
            finally
            {
                _shadowSlots.Release();
            }
        }, CancellationToken.None);
    }
}
