using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Models;
using AgentLilara.PluginSDK;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SessionFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lilara-sessions-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private string FileFor(string session, string file) => Path.Combine(_root, session, file);

    [Fact]
    public async Task SpecAndStateRetainFormatAndRoundTripValues()
    {
        var store = new SessionFileStore(_root, "one");
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        await store.SaveSpecAsync(new CreateSessionSpec
        {
            Type = "task", ParentSessionId = "parent", ChannelId = 42,
            ResolvedComponentIds = new[] { "basic-tools" }, AutoClaimChannel = true
        }, created);
        await store.SaveStateAsync(new SessionStateSnapshot
        {
            SessionId = "one", Status = SessionStatus.Running, CreatedAt = created,
            RoundCount = 7, ActiveCoreConfig = "TaskCore", ConnectedChannelId = "42",
            ChannelCursors = new() { [42] = 99 }, ChildSessionIds = new[] { "child" },
            Axes = new() { ["network"] = "off" }, ForwardedTo = "next"
        });
        var (spec, state, history) = store.Load();
        Assert.Equal("task", spec.Type);
        Assert.Equal("parent", spec.ParentSessionId);
        Assert.Equal(new[] { "basic-tools" }, spec.ResolvedComponentIds);
        Assert.True(spec.AutoClaimChannel);
        Assert.Equal(created, state.CreatedAt);
        Assert.Equal(7, state.RoundCount);
        Assert.Equal(99, state.ChannelCursors[42]);
        Assert.Equal("off", state.Axes["network"]);
        Assert.Equal("next", state.ForwardedTo);
        Assert.Empty(history);
        Assert.Equal(2, JObject.Parse(await File.ReadAllTextAsync(FileFor("one", "spec.json")))["formatVersion"]!.Value<int>());
        Assert.Equal(3, JObject.Parse(await File.ReadAllTextAsync(FileFor("one", "state.json")))["formatVersion"]!.Value<int>());
        Assert.False(File.Exists(FileFor("one", "state.json.tmp")));
    }

    [Fact]
    public async Task PartialUpdatesPreserveUnknownFieldsAndStayInsideInjectedRoot()
    {
        var first = new SessionFileStore(_root, "parent");
        var child = new SessionFileStore(_root, "child");
        var otherRoot = Path.Combine(_root, "other");
        var other = new SessionFileStore(otherRoot, "child");
        const string raw = """{"status":"Running","effectiveParentId":"old","extension":{"keep":42}}""";
        await File.WriteAllTextAsync(FileFor("child", "state.json"), raw);
        await File.WriteAllTextAsync(Path.Combine(otherRoot, "child", "state.json"), raw);
        await child.UpdateStatusAsync(SessionStatus.Completed);
        await first.UpdateEffectiveParentAsync("child", "new-parent");
        var updated = JObject.Parse(await File.ReadAllTextAsync(FileFor("child", "state.json")));
        Assert.Equal("Completed", updated["status"]!.Value<string>());
        Assert.Equal("new-parent", updated["effectiveParentId"]!.Value<string>());
        Assert.Equal(42, updated["extension"]!["keep"]!.Value<int>());
        Assert.Equal(raw, await File.ReadAllTextAsync(Path.Combine(otherRoot, "child", "state.json")));
        await first.UpdateEffectiveParentAsync("missing", "new-parent");
        await first.UpdateStatusAsync(SessionStatus.Completed);
        Assert.False(File.Exists(FileFor("parent", "state.json")));
    }

    [Fact]
    public async Task HistoryTrimsLargePayloadWithoutMutatingInputAndDropsIncompleteTrailingToolCall()
    {
        var store = new SessionFileStore(_root, "one");
        await store.SaveSpecAsync(new CreateSessionSpec { Type = "channel" }, DateTime.UtcNow);
        var large = new string('x', 65537);
        var image = new ContentPart { Type = "image", ImageBase64 = large, ImagePath = "image.png" };
        var tool = new ContentPart { Type = "tool_result", ToolUseId = "id", ToolInput = large };
        await store.AppendHistoryAsync(new()
        {
            new Message { Role = "user", ContentParts = new() { image, tool } },
            new Message { Role = "assistant", ContentParts = new() { new ContentPart { Type = "tool_use", ToolUseId = "pending" } } }
        });
        await File.AppendAllTextAsync(FileFor("one", "history.jsonl"), "broken-json\n");
        var history = store.Load().History;
        var message = Assert.Single(history);
        Assert.Equal("image.png", message.ContentParts![0].ImagePath);
        Assert.Contains("65537", message.ContentParts[0].ImageBase64);
        Assert.Contains("65537", message.ContentParts[1].ToolInput);
        Assert.Equal(large, image.ImageBase64);
        Assert.Equal(large, tool.ToolInput);
    }

    [Fact]
    public async Task HistoryKeepsOnlyRecentThousandNonemptyLines()
    {
        var store = new SessionFileStore(_root, "one");
        await store.SaveSpecAsync(new CreateSessionSpec(), DateTime.UtcNow);
        await store.AppendHistoryAsync(Enumerable.Range(0, 1005)
            .Select(i => new Message { Role = "user", Content = i.ToString() }).ToList());
        var history = store.Load().History;
        Assert.Equal(1000, history.Count);
        Assert.Equal("5", history[0].Content);
        Assert.Equal("1004", history[^1].Content);
    }

    [Fact]
    public async Task MissingOrCorruptStateFallsBackWithoutRewritingAndLegacyStateRemainsReadable()
    {
        var store = new SessionFileStore(_root, "one");
        Assert.Throws<InvalidOperationException>(() => store.Load());
        await store.SaveSpecAsync(new CreateSessionSpec(), DateTime.UtcNow);
        Assert.Equal(SessionStatus.Crashed, store.Load().State.Status);
        Assert.False(File.Exists(FileFor("one", "state.json")));
        await File.WriteAllTextAsync(FileFor("one", "state.json"), "broken-json");
        Assert.Equal(SessionStatus.Crashed, store.Load().State.Status);
        const string legacy = """{"formatVersion":1,"status":"Running","roundCount":4}""";
        await File.WriteAllTextAsync(FileFor("one", "state.json"), legacy);
        Assert.Equal(4, store.Load().State.RoundCount);
        Assert.Empty(store.Load().State.Axes);
        Assert.Equal(legacy, await File.ReadAllTextAsync(FileFor("one", "state.json")));
        Assert.Equal("Lilara.Session", typeof(SessionFileStore).Assembly.GetName().Name);
        Assert.Same(typeof(SessionFileStore).Assembly, typeof(SessionStateSnapshot).Assembly);
        Assert.Same(typeof(SessionFileStore).Assembly, typeof(SnapshotWriter).Assembly);
    }
}
