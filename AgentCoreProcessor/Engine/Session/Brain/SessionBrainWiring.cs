using AgentCoreProcessor.Logging;
using AgentLilara.AlcBridge;
using AgentLilara.SessionBrain;

namespace AgentCoreProcessor.Engine.Session.Brain;

/// <summary>
/// 启动期接线：把 <c>LILARA_SESSION_BRAIN</c> 映射成接缝运行器。
///
/// <para>⚠️ 这里是 ALP 唯一引用 AlcBridge 的地方——其余代码只认
/// <see cref="SessionBrainRuntime"/> 与中性契约。</para>
///
/// <para>⚠️ **配置写错要当场失败**，不能静默退回 Off：那样运维会以为影子在跑，
/// 而实际上一个报告都不会出现。唯一的例外是「压根没设」——那是明确的默认值。</para>
/// </summary>
internal static class SessionBrainWiring
{
    public const string ModeEnv = "LILARA_SESSION_BRAIN";
    public const string ReportEnv = "LILARA_SESSION_BRAIN_REPORT";

    public static void ConfigureFromEnvironment(string storagePath)
    {
        var raw = Environment.GetEnvironmentVariable(ModeEnv);
        var mode = ParseMode(raw);

        if (mode == SessionBrainMode.Off)
        {
            SessionBrainRuntime.Host = SessionBrainHost.Off;
            return;
        }

        var reportPath = Environment.GetEnvironmentVariable(ReportEnv);
        if (string.IsNullOrWhiteSpace(reportPath))
            reportPath = Path.Combine(storagePath, "KernelShadow", "reports.jsonl");

        var brain = new KernelShadowBrain(OnReport, reportPath);
        SessionBrainRuntime.Host = new SessionBrainHost(
            mode,
            brain,
            (message, error) => Signal.Warn(LogGroup.Engine, "会话内核接缝失败",
                new { message, error = error?.Message }));

        // 只读的线上请求体捕获：只有影子模式才打开。
        Client.WireRequestCapture.Enabled = mode == SessionBrainMode.Shadow;

        Signal.Event(LogGroup.Engine, "会话内核接缝已启用", new { mode = mode.ToString(), reportPath });
    }

    internal static SessionBrainMode ParseMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return SessionBrainMode.Off;

        return raw.Trim().ToLowerInvariant() switch
        {
            "off" => SessionBrainMode.Off,
            "shadow" => SessionBrainMode.Shadow,
            // ⚠️ Live 还没有实现。把它当成 Off 会让人以为切流成功——必须响亮地拒绝。
            "live" => throw new InvalidOperationException(
                $"{ModeEnv}=live 尚未实现；切流前请使用 shadow。"),
            _ => throw new InvalidOperationException(
                $"{ModeEnv}='{raw}' 不是合法取值（off / shadow）。"),
        };
    }

    private static void OnReport(ShadowReport report)
    {
        if (report.Equivalent)
        {
            Signal.Event(LogGroup.Engine, "内核影子请求等价",
                new { sessionId = report.AlpSessionId, report.Unsupported.Count });
            return;
        }

        Signal.Warn(LogGroup.Engine, "内核影子请求存在差异", new
        {
            sessionId = report.AlpSessionId,
            engineType = report.EngineType,
            differences = report.Differences.Count,
            wireDifferences = report.WireDifferences.Count,
            unsupported = report.Unsupported.Count,
            first = report.Differences.FirstOrDefault() ?? report.WireDifferences.FirstOrDefault(),
        });
    }
}
