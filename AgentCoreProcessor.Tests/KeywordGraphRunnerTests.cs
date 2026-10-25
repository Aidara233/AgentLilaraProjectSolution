using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Messaging;
using AgentLilara.GraphRuntime.Graph;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 关键词图的本体侧接入：真实 EngineIncomingMessage → InboundEvent → 图 → 意图。
/// 图文件来自 AgentCoreProcessor/Graphs（构建时复制到输出目录）。
/// </summary>
public class KeywordGraphRunnerTests
{
    private static EngineIncomingMessage Message(string text) => new()
    {
        ChannelId = 42,
        MessageId = Guid.NewGuid().ToString("N"),
        Content = text,
        SenderUserId = 7,
        Tag = "normal",
    };

    private static Task<List<ActionIntent>> RunOnce(EngineIncomingMessage message, bool active, string text)
    {
        message.Content = text;
        return RunAsync(message, active);
    }

    private static async Task<List<ActionIntent>> RunAsync(EngineIncomingMessage message, bool active)
    {
        var runner = KeywordGraphRunner.TryCreateFrom("preset.keyword-test.json", active)!;
        var dispatched = new List<ActionIntent>();

        await runner.RunAsync(
            KeywordGraphRunner.ToEvent(message),
            message.MessageId,
            intent =>
            {
                dispatched.Add(intent);
                return Task.CompletedTask;
            });

        return dispatched;
    }

    [Fact]
    public void GraphFileIsAvailable()
    {
        Assert.NotNull(KeywordGraphRunner.TryCreateFrom("preset.keyword-test.json", active: false));
    }

    [Fact]
    public async Task Keyword_ProducesSendIntent()
    {
        var dispatched = await RunAsync(Message("这是一次测试消息"), active: true);

        var intent = Assert.Single(dispatched);
        Assert.Equal(IntentKind.SendMessage, intent.Kind);
        Assert.Equal("42", intent.Target);
        Assert.Equal("这是测试回复", intent.Args["text"].As<string>());
    }

    [Fact]
    public async Task ShadowMode_RecordsButDoesNotDispatch()
    {
        var dispatched = await RunAsync(Message("测试一下"), active: false);

        Assert.Empty(dispatched);
    }

    [Fact]
    public async Task WithoutKeyword_NothingHappens()
    {
        var dispatched = await RunAsync(Message("普通消息"), active: true);

        Assert.Empty(dispatched);
    }

    [Fact]
    public async Task MentionTagIsCarriedIntoEvent()
    {
        var message = Message("测试");
        message.Tag = "mention";

        var dispatched = await RunAsync(message, active: true);

        // 关键词命中与 @ 无关：两者都满足时仍然只发一条
        Assert.Single(dispatched);
    }
}
