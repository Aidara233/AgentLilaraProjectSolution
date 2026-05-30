using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Modules;

/// <summary>
/// 委托状态通知模块。将本循环发起的委托的状态变更注入上下文。
/// </summary>
internal class DelegationNotificationModule : EngineModule
{
    public override string Name => "委托通知";
    public override int InjectPriority => 39;

    private IAgentMessaging? _messaging;
    private SignalFilterConfig? _filterConfig;

    public void SetMessaging(IAgentMessaging messaging) => _messaging = messaging;
    public void SetFilterConfig(SignalFilterConfig config) => _filterConfig = config;

    public override void Attach(ILoopBus bus) { }

    public override Task<string?> BuildRoundInjectAsync(InjectContext ctx)
    {
        if (_messaging == null) return Task.FromResult<string?>(null);

        var notifications = _messaging.DrainNotifications();
        if (notifications.Count == 0) return Task.FromResult<string?>(null);

        if (_filterConfig != null && !_filterConfig.IsVisible(SignalCategory.Delegation))
            return Task.FromResult<string?>(null);

        Signal.Event(LogGroup.Engine, "委托通知注入",
            new { count = notifications.Count, mode = ctx.Mode });

        var sb = new StringBuilder();
        sb.AppendLine("[委托状态更新]");
        foreach (var n in notifications)
        {
            var stateStr = FormatState(n.NewState, n.ResponseType);
            sb.AppendLine($"- 委托 request_id={n.RequestId}「{n.Title}」: {stateStr}");
            if (!string.IsNullOrEmpty(n.ResponderId))
                sb.AppendLine($"  回应者: {n.ResponderId}");
            if (!string.IsNullOrEmpty(n.Content))
                sb.AppendLine($"  内容: {Truncate(n.Content, 300)}");
        }

        return Task.FromResult<string?>(sb.ToString());
    }

    private static string FormatState(string state, string responseType)
    {
        if (state == "Submitted") return "收到新委托请求";
        return responseType switch
        {
            "Accept" => "已被接受",
            "Reject" => "已被拒绝",
            "Progress" => "执行中（进度更新）",
            "Complete" => "已完成",
            "Failed" => "执行失败",
            _ => state
        };
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "...";
}
