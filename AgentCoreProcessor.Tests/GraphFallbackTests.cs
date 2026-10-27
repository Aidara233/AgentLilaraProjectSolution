using AgentCoreProcessor.Engine.Graph;
using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 失败兜底：失败变成一条 runtime.error 事件，只激活 trigger=error 的挂载；
/// 频道级错误图优先，其次 global；错误图自己失败不会再生事件（一跳终止在调用方）。
/// </summary>
public class GraphFallbackTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-fallback-" + Guid.NewGuid().ToString("N"));

    private string GraphDir => Path.Combine(_root, "Graphs");

    public GraphFallbackTests()
    {
        Directory.CreateDirectory(GraphDir);
    }

    private void WriteMounts(string json) =>
        File.WriteAllText(Path.Combine(GraphDir, "mounts.json"), json);

    private static InboundEvent Message() =>
        InboundEvent.Message("test", "1", "普通消息", ChannelKind.Group);

    private static InboundEvent Error() =>
        InboundEvent.RuntimeError("1", "route-rejected:channel");

    [Fact]
    public async Task ErrorGraphIgnoresMessageEvents()
    {
        WriteMounts("""
        { "version": 1, "mounts": [
            { "scope": "channel", "scopeKey": "1", "classId": "preset.fallback", "trigger": "error", "enabled": true }
        ] }
        """);

        var manager = GraphManager.TryCreate(_root, active: true)!;
        var fallback = Assert.Single(manager.RunnersForError(1));

        Assert.Empty(await fallback.EvaluateAsync(Message(), "fallback-msg"));
    }

    [Fact]
    public async Task ErrorGraphProducesIntentsForErrorEvents()
    {
        WriteMounts("""
        { "version": 1, "mounts": [
            { "scope": "channel", "scopeKey": "1", "classId": "preset.fallback", "trigger": "error", "enabled": true }
        ] }
        """);

        var manager = GraphManager.TryCreate(_root, active: true)!;
        var fallback = Assert.Single(manager.RunnersForError(1));

        var intents = await fallback.EvaluateAsync(Error(), "fallback-err");

        var intent = Assert.Single(intents);
        Assert.Equal(IntentKind.SendMessage, intent.Kind);
        Assert.Equal("1", intent.Target);
    }

    [Fact]
    public async Task MessageGraphIgnoresErrorEvents()
    {
        WriteMounts("""
        { "version": 1, "mounts": [
            { "scope": "channel", "scopeKey": "1", "classId": "preset.activation", "enabled": true }
        ] }
        """);

        var manager = GraphManager.TryCreate(_root, active: true)!;
        var runners = manager.RunnersFor(1);

        var messageRunners = Assert.Single(runners);
        Assert.Empty(await messageRunners.EvaluateAsync(Error(), "activation-err"));
    }

    [Fact]
    public void MessageAndErrorMountsDoNotMixInResolution()
    {
        WriteMounts("""
        { "version": 1, "mounts": [
            { "scope": "channel", "scopeKey": "1", "classId": "preset.activation", "enabled": true },
            { "scope": "global",  "classId": "preset.fallback", "trigger": "error", "enabled": true }
        ] }
        """);

        var manager = GraphManager.TryCreate(_root, active: true)!;

        var messageRunner = Assert.Single(manager.RunnersFor(1));
        var errorRunner = Assert.Single(manager.RunnersForError(1));

        Assert.Contains("preset.activation", messageRunner.Instance.InstanceKey);
        Assert.Contains("preset.fallback", errorRunner.Instance.InstanceKey);
    }

    [Fact]
    public void ChannelErrorMountOverridesGlobalFallback()
    {
        File.WriteAllText(Path.Combine(GraphDir, "user.error-quiet.json"),
            """{"classId":"user.error-quiet","kind":"static","nodes":[{"id":"in","type":"on.error"}],"links":[]}""");
        WriteMounts("""
        { "version": 1, "mounts": [
            { "scope": "channel", "scopeKey": "1", "classId": "user.error-quiet", "trigger": "error", "instanceName": "local", "enabled": true },
            { "scope": "global",  "classId": "preset.fallback", "trigger": "error", "enabled": true }
        ] }
        """);

        var manager = GraphManager.TryCreate(_root, active: true)!;

        var errorRunner = Assert.Single(manager.RunnersForError(1));
        Assert.Contains("user.error-quiet", errorRunner.Instance.InstanceKey);

        // 没有频道级错误图的频道，回退到 global 兜底
        var other = Assert.Single(manager.RunnersForError(9));
        Assert.Contains("preset.fallback", other.Instance.InstanceKey);
    }

    [Fact]
    public void UnknownTriggerIsRejected()
    {
        WriteMounts("""
        { "version": 1, "mounts": [
            { "scope": "channel", "scopeKey": "1", "classId": "preset.fallback", "trigger": "nonsense", "enabled": true }
        ] }
        """);

        var manager = GraphManager.TryCreate(_root, active: true)!;

        Assert.Empty(manager.RunnersFor(1));
        Assert.Empty(manager.RunnersForError(1));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
