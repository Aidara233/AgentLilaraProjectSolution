using AgentCoreProcessor.Engine.Graph;
using AgentLilara.GraphRuntime.Contracts;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 挂载表与实例隔离：每个挂载点一个 GraphInstance，变量互不串；实例跟随挂载记录生命周期。
/// 图文件从输出目录的 Graphs/ 取（preset.activation.json 带 pending 变量）。
/// </summary>
public class GraphManagerTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "lilara-mount-" + Guid.NewGuid().ToString("N"));

    private readonly string _graphs;

    public GraphManagerTests()
    {
        _graphs = Path.Combine(_dir, "Graphs");
        Directory.CreateDirectory(_graphs);
        WriteMounts("1", "2");
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

    private void WriteMounts(params string[] channelKeys)
    {
        var entries = string.Join(",", channelKeys.Select(k =>
            $"{{\"scope\":\"channel\",\"scopeKey\":\"{k}\",\"classId\":\"preset.activation\",\"enabled\":true}}"));

        File.WriteAllText(
            Path.Combine(_graphs, "mounts.json"),
            $"{{ \"version\": 1, \"mounts\": [{entries}] }}");
    }

    private static InboundEvent Message() =>
        InboundEvent.Message("test", "chan", "普通消息", ChannelKind.Group, mentionsBot: false);

    private static async Task FeedAsync(KeywordGraphRunner runner, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await runner.DecideAsync(Message(), $"t{i}");
        }
    }

    [Fact]
    public void NoMountsFile_ReturnsNull_AndDoesNotIntervene()
    {
        File.Delete(Path.Combine(_graphs, "mounts.json"));
        Assert.Null(GraphManager.TryCreate(_dir, active: true));
    }

    [Fact]
    public void UnmountedChannel_ReturnsNull()
    {
        var manager = GraphManager.TryCreate(_dir, active: false)!;
        Assert.Null(manager.For(99));
    }

    [Fact]
    public void EachChannelGetsItsOwnInstance()
    {
        var manager = GraphManager.TryCreate(_dir, active: false)!;

        var first = manager.For(1)!;
        var second = manager.For(2)!;

        Assert.NotSame(first, second);
        Assert.Equal(2, manager.InstanceCount);
    }

    [Fact]
    public async Task VariablesAreIsolatedPerChannel()
    {
        var manager = GraphManager.TryCreate(_dir, active: false)!;
        var channelOne = manager.For(1)!;
        var channelTwo = manager.For(2)!;

        await FeedAsync(channelOne, 5);

        // 只有 channel 1 的 pending 增长
        Assert.Equal(5, channelOne.Instance.ReadVariable("pending").AsInt());
        Assert.Equal(0, channelTwo.Instance.ReadVariable("pending").AsInt());
    }

    [Fact]
    public async Task ReleaseDestroysInstanceAndVariables()
    {
        var manager = GraphManager.TryCreate(_dir, active: false)!;
        var before = manager.For(1)!;
        await FeedAsync(before, 3);
        Assert.Equal(3, before.Instance.ReadVariable("pending").AsInt());

        manager.Release(1);

        var after = manager.For(1)!;
        Assert.NotSame(before, after);
        Assert.Equal(0, after.Instance.ReadVariable("pending").AsInt());
    }

    [Fact]
    public void MountChangeRebuildsInstances()
    {
        var manager = GraphManager.TryCreate(_dir, active: false)!;
        Assert.Null(manager.For(3));

        // 改挂载表（新增频道 3），下次 For 应重建
        WriteMounts("1", "3");
        File.SetLastWriteTimeUtc(Path.Combine(_graphs, "mounts.json"), DateTime.UtcNow.AddSeconds(5));

        Assert.NotNull(manager.For(3));
        Assert.Null(manager.For(2)); // 被移除的挂载点不再有图
    }
}
