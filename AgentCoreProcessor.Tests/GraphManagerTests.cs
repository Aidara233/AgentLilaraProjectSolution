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

    private void WriteMounts(params string[] channelKeys) =>
        WriteMountEntries(channelKeys.Select(k => ("channel", k)).ToArray());

    private void WriteMountEntries(params (string Scope, string Key)[] mounts)
    {
        File.WriteAllText(Path.Combine(_graphs, "user.session-counter.json"), """
            {"classId":"user.session-counter","kind":"static",
             "variables":[{"name":"pending","type":"int","initial":0}],
             "nodes":[{"id":"in","type":"on.message"},{"id":"read","type":"read.variable","params":{"name":"pending"}},
               {"id":"one","type":"read.const","params":{"value":1}},{"id":"add","type":"math.add"},
               {"id":"write","type":"write.variable","params":{"name":"pending"}}],
             "links":[{"from":"read.value","to":"add.a"},{"from":"one.value","to":"add.b"},{"from":"add.result","to":"write.value"}]}
            """);
        var entries = string.Join(",", mounts.Select(m =>
        {
            var classId = m.Scope == "channel" && m.Key == "quiet"
                ? "preset.quiet"
                : m.Scope == "session" ? "user.session-counter" : "preset.activation";
            return $"{{\"scope\":\"{m.Scope}\",\"scopeKey\":\"{m.Key}\",\"classId\":\"{classId}\",\"enabled\":true}}";
        }));

        WriteRaw($"{{ \"version\": 1, \"mounts\": [{entries}] }}");
    }

    private void WriteRaw(string json)
    {
        File.WriteAllText(Path.Combine(_graphs, "mounts.json"), json);
        File.SetLastWriteTimeUtc(Path.Combine(_graphs, "mounts.json"), DateTime.UtcNow.AddSeconds(5));
    }

    private static InboundEvent Message() =>
        InboundEvent.Message("test", "chan", "普通消息", ChannelKind.Group, mentionsBot: false);

    private static async Task FeedAsync(KeywordGraphRunner runner, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await runner.EvaluateAsync(Message(), $"t{i}");
        }
    }

    [Fact]
    public void NoMountsFile_ReturnsNull_AndDoesNotIntervene()
    {
        File.Delete(Path.Combine(_graphs, "mounts.json"));
        Assert.Null(GraphManager.TryCreate(_dir, active: true));
    }

    [Fact]
    public void UnmountedChannel_ReturnsEmpty()
    {
        var manager = GraphManager.TryCreate(_dir, active: false)!;
        Assert.Empty(manager.RunnersFor(99));
    }

    [Fact]
    public void EachChannelGetsItsOwnInstance()
    {
        var manager = GraphManager.TryCreate(_dir, active: false)!;

        var first = Assert.Single(manager.RunnersFor(1));
        var second = Assert.Single(manager.RunnersFor(2));

        Assert.NotSame(first, second);
        Assert.Equal(2, manager.InstanceCount);
    }

    [Fact]
    public async Task VariablesAreIsolatedPerChannel()
    {
        var manager = GraphManager.TryCreate(_dir, active: false)!;
        var channelOne = Assert.Single(manager.RunnersFor(1));
        var channelTwo = Assert.Single(manager.RunnersFor(2));

        await FeedAsync(channelOne, 5);

        // 只有 channel 1 的 pending 增长
        Assert.Equal(5, channelOne.Instance.ReadVariable("pending").AsInt());
        Assert.Equal(0, channelTwo.Instance.ReadVariable("pending").AsInt());
    }

    [Fact]
    public async Task ReleaseDestroysInstanceAndVariables()
    {
        var manager = GraphManager.TryCreate(_dir, active: false)!;
        var before = Assert.Single(manager.RunnersFor(1));
        await FeedAsync(before, 3);
        Assert.Equal(3, before.Instance.ReadVariable("pending").AsInt());

        manager.Release(1);

        var after = Assert.Single(manager.RunnersFor(1));
        Assert.NotSame(before, after);
        Assert.Equal(0, after.Instance.ReadVariable("pending").AsInt());
    }

    [Fact]
    public async Task QuietPresetProducesNoIntents()
    {
        WriteRaw("{ \"version\": 1, \"mounts\": [{\"scope\":\"channel\",\"scopeKey\":\"1\",\"classId\":\"preset.quiet\",\"enabled\":true}] }");
        var manager = GraphManager.TryCreate(_dir, active: true)!;
        var quiet = Assert.Single(manager.RunnersFor(1));

        var intents = await quiet.EvaluateAsync(Message(), "quiet-test");

        Assert.Empty(intents);
    }

    [Fact]
    public async Task SameClassCanBeMountedTwiceOnOneChannel()
    {
        WriteRaw("""
        {
          "version": 1,
          "mounts": [
            { "scope": "channel", "scopeKey": "1", "classId": "preset.activation", "instanceName": "a", "order": 1, "enabled": true },
            { "scope": "channel", "scopeKey": "1", "classId": "preset.activation", "instanceName": "b", "order": 2, "enabled": true }
          ]
        }
        """);

        var manager = GraphManager.TryCreate(_dir, active: false)!;
        var runners = manager.RunnersFor(1);

        Assert.Equal(2, runners.Count);
        Assert.NotSame(runners[0], runners[1]);

        // 各自独立变量
        await FeedAsync(runners[1], 4);
        Assert.Equal(0, runners[0].Instance.ReadVariable("pending").AsInt());
        Assert.Equal(4, runners[1].Instance.ReadVariable("pending").AsInt());

        // 实例键带实例名，互不覆盖
        Assert.Contains("#a", runners[0].Instance.InstanceKey);
        Assert.Contains("#b", runners[1].Instance.InstanceKey);
    }

    [Fact]
    public void MountsRunInOrder()
    {
        WriteRaw("""
        {
          "version": 1,
          "mounts": [
            { "scope": "channel", "scopeKey": "1", "classId": "preset.activation", "instanceName": "late",  "order": 20, "enabled": true },
            { "scope": "channel", "scopeKey": "1", "classId": "preset.activation", "instanceName": "early", "order": 5,  "enabled": true }
          ]
        }
        """);

        var manager = GraphManager.TryCreate(_dir, active: false)!;
        var runners = manager.RunnersFor(1);

        Assert.Equal(2, runners.Count);
        Assert.Contains("#early", runners[0].Instance.InstanceKey);
        Assert.Contains("#late", runners[1].Instance.InstanceKey);
    }

    [Fact]
    public void DuplicateInstanceKeyIsSkipped()
    {
        WriteRaw("""
        {
          "version": 1,
          "mounts": [
            { "scope": "channel", "scopeKey": "1", "classId": "preset.activation", "enabled": true },
            { "scope": "channel", "scopeKey": "1", "classId": "preset.activation", "enabled": true }
          ]
        }
        """);

        var manager = GraphManager.TryCreate(_dir, active: false)!;

        // 同名同作用域且都没写 instanceName → 实例键相同，第二个被丢弃
        Assert.Single(manager.RunnersFor(1));
    }

    [Fact]
    public void GlobalMountsApplyWhenChannelHasNoOwnMounts()
    {
        WriteRaw("""
        {
          "version": 1,
          "mounts": [
            { "scope": "global", "classId": "preset.quiet", "enabled": true },
            { "scope": "channel", "scopeKey": "7", "classId": "preset.activation", "enabled": true }
          ]
        }
        """);

        var manager = GraphManager.TryCreate(_dir, active: false)!;

        var channelSeven = Assert.Single(manager.RunnersFor(7));
        var otherChannel = Assert.Single(manager.RunnersFor(8));

        Assert.Contains("preset.activation", channelSeven.Instance.InstanceKey);
        Assert.Contains("preset.quiet", otherChannel.Instance.InstanceKey);
    }

    [Fact]
    public void SessionMountCreatesOnlyForExactSessionId()
    {
        WriteMountEntries(("session", "session-a"));
        var manager = GraphManager.TryCreate(_dir, active: false)!;

        var session = Assert.Single(manager.RunnersForSession("session-a"));

        Assert.Contains("@session#session-a", session.Instance.InstanceKey);
        Assert.Empty(manager.RunnersForSession("session-b"));
        Assert.Equal(1, manager.InstanceCount);
    }

    [Fact]
    public async Task CompletingOneSessionReleasesOnlyItsSessionGraphInstance()
    {
        WriteMountEntries(("session", "session-a"), ("session", "session-b"));
        var manager = GraphManager.TryCreate(_dir, active: false)!;

        var sessionA = Assert.Single(manager.RunnersForSession("session-a"));
        var sessionB = Assert.Single(manager.RunnersForSession("session-b"));
        await FeedAsync(sessionA, 4);

        Assert.Equal(4, sessionA.Instance.ReadVariable("pending").AsInt());
        Assert.Equal(0, sessionB.Instance.ReadVariable("pending").AsInt());

        manager.ReleaseSession("session-a");

        Assert.Equal(1, manager.InstanceCount);
        Assert.Same(sessionB, Assert.Single(manager.RunnersForSession("session-b")));
        var recreatedA = Assert.Single(manager.RunnersForSession("session-a"));
        Assert.NotSame(sessionA, recreatedA);
        Assert.Equal(0, recreatedA.Instance.ReadVariable("pending").AsInt());
    }

    [Fact]
    public void SessionReleaseDoesNotDestroyChannelInstanceOrOtherSessionInstance()
    {
        WriteMountEntries(("channel", "1"), ("session", "session-a"), ("session", "session-b"));
        var manager = GraphManager.TryCreate(_dir, active: false)!;

        var channel = Assert.Single(manager.RunnersFor(1));
        var sessionA = Assert.Single(manager.RunnersForSession("session-a"));
        var sessionB = Assert.Single(manager.RunnersForSession("session-b"));
        Assert.Equal(3, manager.InstanceCount);

        manager.ReleaseSession("session-a");

        Assert.Equal(2, manager.InstanceCount);
        Assert.Same(channel, Assert.Single(manager.RunnersFor(1)));
        Assert.Same(sessionB, Assert.Single(manager.RunnersForSession("session-b")));
        Assert.NotSame(sessionA, Assert.Single(manager.RunnersForSession("session-a")));
    }

    [Fact]
    public void MountChangeRebuildsInstances()
    {
        var manager = GraphManager.TryCreate(_dir, active: false)!;
        Assert.Empty(manager.RunnersFor(3));

        // 改挂载表（新增频道 3），下次 RunnersFor 应重建
        WriteMounts("1", "3");

        Assert.NotEmpty(manager.RunnersFor(3));
        Assert.Empty(manager.RunnersFor(2)); // 被移除的挂载点不再有图
    }
}
