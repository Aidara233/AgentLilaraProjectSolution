using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphSandbox;
using Xunit;

namespace AgentLilara.GraphSandbox.Tests;

public sealed class FilePollerTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "lilara-sandbox-" + Guid.NewGuid().ToString("N"));

    public FilePollerTests()
    {
        System.IO.Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响断言
        }
    }

    [Fact]
    public void ReturnsOnlyNewFiles()
    {
        var poller = new FilePoller(_directory, "*.txt");

        File.WriteAllText(Path.Combine(_directory, "a.txt"), "hello");
        Assert.Single(poller.Poll());

        Assert.Empty(poller.Poll());

        File.WriteAllText(Path.Combine(_directory, "b.txt"), "world");
        var second = poller.Poll();
        Assert.Single(second);
        Assert.Equal("b.txt", second[0].FileName);
    }

    [Fact]
    public void RewriteProducesNewMessage()
    {
        var path = Path.Combine(_directory, "same.txt");
        File.WriteAllText(path, "first");
        var poller = new FilePoller(_directory, "*.txt");
        Assert.Single(poller.Poll());

        File.WriteAllText(path, "second");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));

        var next = poller.Poll();
        Assert.Single(next);
        Assert.Equal("second", next[0].Text);
    }

    [Fact]
    public void MissingDirectoryReturnsEmpty()
    {
        var poller = new FilePoller(Path.Combine(_directory, "nope"), "*.txt");
        Assert.Empty(poller.Poll());
    }
}

public sealed class SandboxRunnerTests
{
    private static SandboxRunner NewRunner() =>
        new(SampleGraphs.LoadPresetMention(), SampleGraphs.LoadPresetCounter());

    private static InboundEvent Message(bool mentionsBot) =>
        InboundEvent.Message("file", "sandbox", "在吗", senderId: "u1", mentionsBot: mentionsBot);

    [Fact]
    public void StaticEmitsSessionPrompt_WhenMentioned()
    {
        var report = NewRunner().Tick(Message(mentionsBot: true), seq: 1);

        Assert.True(report.StaticOk, string.Join(",", report.StaticUnresolved));
        var intent = Assert.Single(report.StaticIntents);
        Assert.Equal(IntentKind.SessionPrompt, intent.Kind);
        Assert.Equal("default", intent.Target);
        Assert.Equal("在吗", intent.Args["text"].As<string>());
    }

    [Fact]
    public void StaticSilent_WhenNotMentioned()
    {
        var report = NewRunner().Tick(Message(mentionsBot: false), seq: 1);

        Assert.True(report.StaticOk, string.Join(",", report.StaticUnresolved));
        Assert.Empty(report.StaticIntents);
    }

    [Fact]
    public void DynamicFiresOnlyOnRisingEdge()
    {
        var runner = NewRunner();
        var reports = Enumerable.Range(1, 6)
            .Select(seq => runner.Tick(null, seq))
            .ToList();

        // 第 4 拍快照读到 3，条件首次成立 → 上升沿，产出一次意图
        Assert.Single(reports[3].DynamicIntents);

        // 第 5、6 拍电平仍为真，但没有上升沿 → 不产出，只记 suppressed
        Assert.Empty(reports[4].DynamicIntents);
        Assert.Empty(reports[5].DynamicIntents);
        Assert.Contains("sink", reports[4].DynamicSuppressed);
    }

    [Fact]
    public void NoEventStillAdvancesClock()
    {
        var report = NewRunner().Tick(null, seq: 1);

        Assert.True(report.StaticOk);
        Assert.Null(report.Event);
        Assert.Empty(report.StaticIntents);
        Assert.Equal(1, report.DynamicVariables["count"].AsInt());
    }
}
