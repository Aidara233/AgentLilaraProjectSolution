using System.Text.Json;
using AgentLilara.SessionBrain;

namespace AgentLilara.AlcBridge;

/// <summary>一次影子比较的结果。</summary>
public sealed record ShadowReport(
    DateTimeOffset At,
    string AlpSessionId,
    string EngineType,
    int LegacyMessageCount,
    int KernelEventCount,
    IReadOnlyList<string> Differences,
    IReadOnlyList<string> Unsupported,
    IReadOnlyList<string> WireDifferences,
    IReadOnlyList<string> WireNotes)
{
    public bool Equivalent => Differences.Count == 0 && WireDifferences.Count == 0;
}

/// <summary>
/// 影子引擎：把 Legacy 刚发过的请求重建成内核请求，比较后报告。
///
/// <para>⚠️ <b>它没有任何外部副作用</b>：不建内核会话、不写数据根、不调用模型、
/// 不执行工具、不改 ALP 历史。唯一的写操作是**可选的报告文件**。</para>
///
/// <para>⚠️ 它<b>不</b>参与 Legacy 的返回路径——<see cref="ISessionBrain.InvokeAsync"/> 的返回值
/// 在这里永远是空的，因为影子结果不喂给会话。</para>
/// </summary>
public sealed class KernelShadowBrain : ISessionBrain
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly Action<ShadowReport>? _onReport;
    private readonly string? _reportPath;
    private readonly object _fileLock = new();

    public KernelShadowBrain(Action<ShadowReport>? onReport = null, string? reportPath = null)
    {
        _onReport = onReport;
        _reportPath = reportPath;
    }

    public string Engine => "kernel-shadow";

    public Task<SessionBrainResult> InvokeAsync(
        SessionBrainRequest request, CancellationToken cancellationToken)
    {
        ShadowReport report;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var build = KernelRequestBuilder.Build(request);
            var comparison = CanonicalComparer.Compare(request, build.Request, build.Unsupported);
            var wire = WireComparer.Compare(request, build.Request);

            report = new ShadowReport(
                DateTimeOffset.UtcNow,
                request.AlpSessionId,
                request.EngineType,
                request.Messages.Count,
                build.Request.Timeline.Count,
                comparison.Differences,
                comparison.Unsupported,
                wire.Differences,
                wire.Notes);
        }
        catch (Exception ex)
        {
            report = new ShadowReport(
                DateTimeOffset.UtcNow,
                request.AlpSessionId,
                request.EngineType,
                request.Messages.Count,
                0,
                [$"shadow-error：{ex.GetType().Name}: {ex.Message}"],
                [],
                [],
                []);
        }

        Emit(report);
        return Task.FromResult(new SessionBrainResult(null, null, [], null));
    }

    private void Emit(ShadowReport report)
    {
        // ⚠️ 报告出口自己的失败同样不能冒到会话里。
        try { _onReport?.Invoke(report); }
        catch { /* 报告出口是观察者，不是业务路径 */ }

        if (string.IsNullOrEmpty(_reportPath)) return;

        try
        {
            var line = JsonSerializer.Serialize(report, JsonOptions);
            lock (_fileLock)
            {
                var directory = Path.GetDirectoryName(_reportPath);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(_reportPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // 同上：报告写不进去不该影响任何事情。
        }
    }
}
