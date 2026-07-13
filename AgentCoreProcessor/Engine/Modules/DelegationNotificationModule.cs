using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Modules;

/// <summary>
/// 循环消息通知模块。把本循环收到的跨循环单发消息注入到模型上下文。
/// </summary>
internal class DelegationNotificationModule : EngineModule
{
    public override string Name => "循环消息通知";
    public override int InjectPriority => 39;

    private IAgentMessaging? _messaging;
    private SignalFilterConfig? _filterConfig;

    public void SetMessaging(IAgentMessaging messaging) => _messaging = messaging;
    public void SetFilterConfig(SignalFilterConfig config) => _filterConfig = config;

    public override void Attach(ILoopBus bus) { }

    public override Task<string?> BuildRoundInjectAsync(InjectContext ctx)
    {
        if (_messaging == null) return Task.FromResult<string?>(null);

        var messages = _messaging.DrainNotifications();
        if (messages.Count == 0) return Task.FromResult<string?>(null);

        if (_filterConfig != null && !_filterConfig.IsVisible(SignalCategory.Delegation))
            return Task.FromResult<string?>(null);

        Signal.Event(LogGroup.Engine, "循环消息通知注入", new { count = messages.Count, mode = ctx.Mode });

        var sb = new StringBuilder();
        sb.AppendLine("[循环消息]");
        foreach (var m in messages)
        {
            var direction = m.TargetId == null ? "广播" : "定向";
            sb.AppendLine($"- 来自 {m.InitiatorId} ({direction}): {Truncate(m.Content, 500)}");
        }

        return Task.FromResult<string?>(sb.ToString());
    }

    private static string Truncate(string s, int maxLen)
        => s.Length <= maxLen ? s : s[..maxLen] + "...";
}