using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.CrossLoopTools;

[Component(Name = "cross-loop", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Enabled)]
public class CrossLoopComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private IAgentMessaging? _messaging;
    private SendMessageTool? _sendMessage;
    private ListLoopsTool? _listLoops;

    public override ComponentMeta Meta => new()
    {
        Name = "cross-loop",
        Description = "跨循环消息（send_message + list_loops）",
        DefaultEnabled = true,
        PromptPriority = 42
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_sendMessage != null) yield return _sendMessage;
            if (_listLoops != null) yield return _listLoops;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;
        _messaging = context.GetService<IAgentMessaging>();

        if (_messaging != null)
        {
            _sendMessage = new SendMessageTool(_messaging);
        }

        // list_loops 仅系统循环可用
        if (_ctx.LoopType == "system" && _messaging != null)
        {
            _listLoops = new ListLoopsTool(_messaging);
        }

        return Task.CompletedTask;
    }

    /// <summary>注入收到的循环消息（每轮 drain 一次，避免重复）。</summary>
    public override string? BuildPromptSection()
    {
        if (_messaging == null) return null;

        var messages = _messaging.DrainNotifications();
        if (messages.Count == 0) return null;

        var parts = new List<string>
        {
            "[循环消息]",
            ""
        };

        foreach (var m in messages)
        {
            var direction = m.TargetId == null ? "广播" : "定向";
            var preview = m.Content.Length <= 500 ? m.Content : m.Content[..500] + "...";
            parts.Add($"- 来自 {m.InitiatorId} ({direction}): {preview}");
        }

        return string.Join("\n", parts);
    }
}