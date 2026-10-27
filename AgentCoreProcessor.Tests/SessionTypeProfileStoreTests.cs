using System.Text.Json.Nodes;
using AgentCoreProcessor.Engine.Session;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class SessionTypeProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lilara-profiles-" + Guid.NewGuid().ToString("N"));
    private string FileFor(string type) => Path.Combine(_directory, type + ".json");
    public SessionTypeProfileStoreTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void StoresOwnIndependentCachesAndInvalidation()
    {
        var first = new SessionTypeProfileStore(_directory);
        var secondDirectory = Path.Combine(_directory, "second");
        Directory.CreateDirectory(secondDirectory);
        File.WriteAllText(FileFor("channel"), """{"Core":"First"}""");
        File.WriteAllText(Path.Combine(secondDirectory, "channel.json"), """{"Core":"Second"}""");
        var second = new SessionTypeProfileStore(secondDirectory);
        var cached = first.Get("channel");
        File.WriteAllText(FileFor("channel"), """{"Core":"Changed"}""");
        Assert.Same(cached, first.Get("channel"));
        Assert.Equal("Second", second.Get("channel").DefaultCoreConfig);
        first.Invalidate();
        Assert.Equal("Changed", first.Get("channel").DefaultCoreConfig);
        Assert.Equal("Second", second.Get("channel").DefaultCoreConfig);
    }

    [Fact]
    public void MissingFieldsAndEmptyArraysPreserveFallbackRulesButEmptyToolsRemainExplicit()
    {
        var store = new SessionTypeProfileStore(_directory);
        var seed = store.Get("channel");
        File.WriteAllText(FileFor("channel"), """{"Core":"Custom","AllowedCores":[],"Components":[],"Prompts":[],"Tools":{"Mode":"whitelist","List":[]}}""");
        store.Invalidate();
        var profile = store.Get("channel");
        Assert.Equal("Custom", profile.DefaultCoreConfig);
        Assert.Equal(new[] { "Custom" }, profile.AllowedCoreConfigs);
        Assert.Equal(seed.Components, profile.Components);
        Assert.Equal(seed.PromptFiles, profile.PromptFiles);
        Assert.Empty(profile.Tools.List);
        Assert.False(profile.IsToolAllowed("speak"));
        File.WriteAllText(FileFor("channel"), "{}");
        store.Invalidate();
        Assert.Equal(seed.DefaultCoreConfig, store.Get("channel").DefaultCoreConfig);
        Assert.Equal(new[] { seed.DefaultCoreConfig }, store.Get("channel").AllowedCoreConfigs);
        Assert.Equal(seed.Tools.List, store.Get("channel").Tools.List);
    }

    [Fact]
    public void SavePreservesOtherFieldsDeduplicatesAndInvalidatesCache()
    {
        File.WriteAllText(FileFor("task"), """{"Type":"task","Core":"Custom","AllowedCores":["A","B"],"Components":["original"],"Prompts":["custom.txt"],"extension":{"value":42}}""");
        var store = new SessionTypeProfileStore(_directory);
        var before = store.Get("task");
        store.Save("task", "BLACKLIST", new[] { "Speak", "speak", "wait" });
        var after = store.Get("task");
        Assert.NotSame(before, after);
        Assert.Equal("blacklist", after.Tools.Mode);
        Assert.Equal(new[] { "Speak", "wait" }, after.Tools.List);
        Assert.False(store.IsAllowed("task", "SPEAK"));
        Assert.True(store.IsAllowed("task", "other"));
        store.SaveComponents("task", new[] { "one", "ONE", "two" });
        var final = store.Get("task");
        Assert.Equal(new[] { "one", "two" }, final.Components);
        Assert.Equal("Custom", final.DefaultCoreConfig);
        Assert.Equal(new[] { "A", "B" }, final.AllowedCoreConfigs);
        Assert.Equal(new[] { "custom.txt" }, final.PromptFiles);
        Assert.Equal(after.Tools.List, final.Tools.List);
        Assert.Equal(42, JsonNode.Parse(File.ReadAllText(FileFor("task")))!["extension"]!["value"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("channel", "ChannelCore")]
    [InlineData("task", "TaskCore")]
    [InlineData("express", "ExpressCore")]
    [InlineData("review", "TaskCore")]
    public void DefaultsCanSeedSavedFiles(string type, string core)
    {
        var store = new SessionTypeProfileStore(_directory);
        var seed = store.Get(type);
        Assert.Equal(core, seed.DefaultCoreConfig);
        store.SaveComponents(type, new[] { "custom" });
        Assert.Equal(seed.Tools.List, store.Get(type).Tools.List);
        Assert.Equal(seed.PromptFiles, store.Get(type).PromptFiles);
        Assert.Equal(seed.AllowedCoreConfigs, store.Get(type).AllowedCoreConfigs);
        var defaults = store.GetDefault(type);
        Assert.Equal(seed.Tools.Mode, defaults.Mode);
        Assert.Equal(seed.Tools.List, defaults.Tools);
    }

    [Fact]
    public void DiscoveryRequiresMatchingDeclarationAndMalformedFilesWarnAndFallback()
    {
        var warnings = new List<string>();
        var store = new SessionTypeProfileStore(_directory, (message, _) => warnings.Add(message));
        File.WriteAllText(FileFor("extra"), """{"Type":"extra"}""");
        File.WriteAllText(FileFor("mismatch"), """{"Type":"elsewhere"}""");
        File.WriteAllText(FileFor("channel"), "broken");
        Assert.Contains("extra", store.All());
        Assert.DoesNotContain("mismatch", store.All());
        Assert.Equal("ChannelCore", store.Get("channel").DefaultCoreConfig);
        Assert.Single(warnings);
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Get("unknown"));
        Assert.True(store.IsAllowed(null, "anything"));
        Assert.True(store.IsAllowed("", "anything"));
        Assert.Equal("channel", store.Get("").Type);
    }

    [Fact]
    public void SaveFailureReportsInjectedWarningWithoutChangingCachedProfile()
    {
        var blocked = Path.Combine(_directory, "blocked");
        File.WriteAllText(blocked, "file instead of directory");
        var warnings = new List<string>();
        var store = new SessionTypeProfileStore(blocked, (message, _) => warnings.Add(message));
        var before = store.Get("task");
        store.Save("task", "blacklist", new[] { "speak" });
        Assert.Single(warnings);
        Assert.Same(before, store.Get("task"));
    }

    [Fact]
    public void ProfilesAndRulesBelongToSessionLibrary()
    {
        var assembly = typeof(SessionTypeProfileStore).Assembly;
        Assert.Equal("Lilara.Session", assembly.GetName().Name);
        Assert.Same(assembly, typeof(SessionTypeProfile).Assembly);
        Assert.Same(assembly, typeof(SessionTools).Assembly);
        var tools = new SessionTools { Mode = "WHITELIST", List = new() { "Speak" } };
        Assert.True(tools.IsAllowed("sPEAK"));
        Assert.False(tools.IsAllowed("wait"));
    }
}
