using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;

namespace AgentCoreProcessor.Engine.Session;

internal partial class RuntimeSession
{
    private Func<IReadOnlyList<KeywordGraphRunner>>? _graphRunnerProvider;

    internal void SetGraphRunnerProvider(Func<IReadOnlyList<KeywordGraphRunner>> provider) =>
        _graphRunnerProvider = provider;

    /// <summary>每批已排空的新消息运行一次；不重新投递原消息，不改变激活判定。</summary>
    internal async Task ConsumeSessionGraphsAsync(
        IReadOnlyList<(int channelId, UserMessage msg, bool isMention)> messages)
    {
        if (messages.Count == 0) return;
        IReadOnlyList<KeywordGraphRunner> runners;
        try { runners = _graphRunnerProvider?.Invoke() ?? _graphRunners; }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "graph.session.mount_failed", new { SessionId, error = ex.Message });
            return;
        }
        if (runners.Count == 0) return;
        // A message can arrive through both normal and mention ports. Deduplicate stable
        // message identities within this drained batch, retaining the combined mention flag.
        var grouped = messages.GroupBy(item => item.msg.Id > 0
            ? $"{item.channelId}:db:{item.msg.Id}"
            : !string.IsNullOrEmpty(item.msg.PlatformMessageId)
                ? $"{item.channelId}:platform:{item.msg.PlatformMessageId}"
                : $"unkeyed:{Guid.NewGuid():N}");
        foreach (var group in grouped)
        {
            var (channelId, message, _) = group.First();
            var mention = group.Any(item => item.isMention);
            var input = InboundEvent.Message("alp-session", channelId.ToString(), message.Content,
                ChannelKind.Group, senderId: message.UserId.ToString(), mentionsBot: mention,
                dedupeKey: message.PlatformMessageId ?? message.Id.ToString());
            foreach (var runner in runners)
            {
                var intents = await runner.EvaluateAsync(input, $"session:{SessionId}:{Guid.NewGuid():N}");
                if (!runner.IsActive) continue;
                foreach (var intent in intents)
                {
                    // Session graphs have an explicit local injection contract. Other host actions
                    // require their own permission and dispatch contract before being enabled.
                    if (intent.Kind != IntentKind.SessionInject
                        || !string.Equals(intent.Target, SessionId, StringComparison.Ordinal))
                    {
                        Signal.Warn(LogGroup.Engine, "graph.session.intent_rejected",
                            new { SessionId, intent.Kind, intent.Target });
                        continue;
                    }
                    var text = intent.Args.TryGetValue("text", out var cell) ? cell.As<string>() : null;
                    if (string.IsNullOrEmpty(text)) continue;
                    var injected = new Message { Role = "user", Content = text };
                    _history.Add(injected);
                    await PersistRoundAsync(new List<Message> { injected });
                }
            }
        }
    }
}
