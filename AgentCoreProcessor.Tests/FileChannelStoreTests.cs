using AgentCoreProcessor.Engine;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class FileChannelStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "channel-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingFileUsesDatabaseAffinityOrDefaults()
    {
        IChannelStore store = new FileChannelStore(_root);
        Assert.Equal(1f, store.LoadConfig(42).Affinity);
        Assert.Equal(0.25f, store.LoadConfig(42, 0.25f).Affinity);
        Assert.Equal("normal", store.LoadConfig(42).Importance);
        Assert.False(File.Exists(Path.Combine(_root, "ChannelState", "42.json")));
    }

    [Fact]
    public void RoundTripPreservesConfigurationAndJsonOverridesDatabaseFallback()
    {
        IChannelStore store = new FileChannelStore(_root);
        store.SaveConfig(42, new ChannelConfig
        {
            Affinity = 0.75f, Importance = "high", WatchKeywords = new() { "hello", "频道" }
        });
        var loaded = new FileChannelStore(_root).LoadConfig(42, 0.1f);
        Assert.Equal(0.75f, loaded.Affinity);
        Assert.Equal("high", loaded.Importance);
        Assert.Equal(new[] { "hello", "频道" }, loaded.WatchKeywords);
        loaded.WatchKeywords.Clear();
        Assert.Equal(2, store.LoadConfig(42).WatchKeywords.Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "ChannelState"), "*.tmp"));
    }

    [Fact]
    public void StoragePathsAndChannelIdsAreIsolated()
    {
        var first = new FileChannelStore(Path.Combine(_root, "first"));
        var second = new FileChannelStore(Path.Combine(_root, "second"));
        first.SaveConfig(42, new ChannelConfig { Affinity = 0.5f });
        second.SaveConfig(42, new ChannelConfig { Affinity = 0.2f });
        Assert.Equal(0.5f, first.LoadConfig(42).Affinity);
        Assert.Equal(0.2f, second.LoadConfig(42).Affinity);
        Assert.Equal(1f, first.LoadConfig(43).Affinity);
    }

    [Fact]
    public void JsonNullKeepsExistingDefaultSemantics()
    {
        var directory = Path.Combine(_root, "ChannelState");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "42.json"), "null");
        Assert.Equal(1f, new FileChannelStore(_root).LoadConfig(42, 0.2f).Affinity);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
