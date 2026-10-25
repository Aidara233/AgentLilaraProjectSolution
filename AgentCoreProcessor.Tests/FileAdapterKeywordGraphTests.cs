using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Engine.Graph;
using AgentLilara.GraphRuntime.Graph;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 用 FileAdapter 走一遍真实入站形状：文件 → IncomingMessage → 图 → 意图。
/// 不启动主引擎、不碰正式 Storage；FileAdapter 的轮询目录指向临时目录。
/// </summary>
public class FileAdapterKeywordGraphTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "lilara-file-" + Guid.NewGuid().ToString("N"));

    public FileAdapterKeywordGraphTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "input"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言
        }
    }

    private async Task<IncomingMessage> ReceiveOne(string content)
    {
        var adapter = new FileAdapter("file-test", _dir, pollIntervalMs: 50);
        IncomingMessage? received = null;
        adapter.OnMessageReceived += m => received = m;

        using var cts = new CancellationTokenSource();
        var running = adapter.StartAsync(cts.Token);

        File.WriteAllText(Path.Combine(_dir, "input", "msg.txt"), content);

        for (var i = 0; i < 60 && received is null; i++)
        {
            await Task.Delay(50);
        }

        cts.Cancel();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
            // 期望路径
        }

        Assert.NotNull(received);
        return received!;
    }

    [Fact]
    public async Task FileMessage_WithKeyword_ProducesSendIntent()
    {
        var message = await ReceiveOne("这是一次测试消息");
        Assert.Contains("测试", message.Content, StringComparison.Ordinal);

        var runner = KeywordGraphRunner.TryCreateFrom("preset.keyword-test.json", active: true)!;
        var dispatched = new List<ActionIntent>();

        await runner.RunAsync(
            KeywordGraphRunner.ToEvent(message, channelId: 42),
            "file-msg",
            intent =>
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
    public async Task FileMessage_WithoutKeyword_ProducesNothing()
    {
        var message = await ReceiveOne("普通消息");

        var runner = KeywordGraphRunner.TryCreateFrom("preset.keyword-test.json", active: true)!;
        var dispatched = new List<ActionIntent>();

        await runner.RunAsync(
            KeywordGraphRunner.ToEvent(message, channelId: 42),
            "file-msg",
            intent =>
            {
                dispatched.Add(intent);
                return Task.CompletedTask;
            });

        Assert.Empty(dispatched);
    }
}
