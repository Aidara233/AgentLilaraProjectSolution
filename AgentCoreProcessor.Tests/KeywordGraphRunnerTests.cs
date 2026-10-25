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

    [Fact]
    public void GraphFileIsAvailable()
    {
        Assert.NotNull(KeywordGraphRunner.TryCreate(active: false));
    }

    [Fact]
    public async Task Keyword_ProducesSendIntent()
    {
        var runner = KeywordGraphRunner.TryCreate(active: true)!;
        var dispatched = new List<ActionIntent>();

        await runner.RunAsync(Message("这是一次测试消息"), intent =>
        {
            dispatched.Add(intent);
            return Task.CompletedTask;
        });

        var intent = Assert.Single(dispatched);
        Assert.Equal(IntentKind.SendMessage, intent.Kind);
        Assert.Equal("42", intent.Target);
        Assert.Equal("这是测试回复", intent.Args["text"].As<string>());
    }

    [Fact]
    public async Task ShadowMode_RecordsButDoesNotDispatch()
    {
        var runner = KeywordGraphRunner.TryCreate(active: false)!;
        var dispatched = new List<ActionIntent>();

        await runner.RunAsync(Message("测试一下"), intent =>
        {
            dispatched.Add(intent);
            return Task.CompletedTask;
        });

        Assert.Empty(dispatched);
    }

    [Fact]
    public async Task WithoutKeyword_NothingHappens()
    {
        var runner = KeywordGraphRunner.TryCreate(active: true)!;
        var dispatched = new List<ActionIntent>();

        await runner.RunAsync(Message("普通消息"), intent =>
        {
            dispatched.Add(intent);
            return Task.CompletedTask;
        });

        Assert.Empty(dispatched);
    }

    [Fact]
    public async Task MentionTagIsCarriedIntoEvent()
    {
        var message = Message("测试");
        message.Tag = "mention";
        var runner = KeywordGraphRunner.TryCreate(active: true)!;
        var dispatched = new List<ActionIntent>();

        await runner.RunAsync(message, intent =>
        {
            dispatched.Add(intent);
            return Task.CompletedTask;
        });

        // 关键词命中与 @ 无关：两者都满足时仍然只发一条
        Assert.Single(dispatched);
    }
}
