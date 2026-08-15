using AgentCoreProcessor.Tool.Host;
using Xunit;

namespace AgentCoreProcessor.Tests;

public class PluginLoaderTests
{
    [Fact]
    public void PlatformMatches_EmptyList_ReturnsTrue()
    {
        var manifest = new PluginManifest { Platform = new() };
        Assert.True(PluginLoader.PlatformMatches(manifest));
    }

    [Fact]
    public void PlatformMatches_ContainsCurrent_ReturnsTrue()
    {
        var manifest = new PluginManifest { Platform = new() { PluginLoader.CurrentPlatform } };
        Assert.True(PluginLoader.PlatformMatches(manifest));
    }

    [Fact]
    public void PlatformMatches_ContainsOtherOnly_ReturnsFalse()
    {
        var other = PluginLoader.CurrentPlatform == "windows" ? "linux" : "windows";
        var manifest = new PluginManifest { Platform = new() { other } };
        Assert.False(PluginLoader.PlatformMatches(manifest));
    }

    [Fact]
    public void PlatformMatches_CaseInsensitive()
    {
        var manifest = new PluginManifest { Platform = new() { PluginLoader.CurrentPlatform.ToUpperInvariant() } };
        Assert.True(PluginLoader.PlatformMatches(manifest));
    }
}
