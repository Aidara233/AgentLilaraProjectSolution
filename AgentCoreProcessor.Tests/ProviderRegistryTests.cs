using AgentCoreProcessor.WebUI.Shell;
using AgentLilara.PluginSDK.WebUI;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// ProviderRegistry 路由匹配与冲突诊断的回归测试。
/// </summary>
public class ProviderRegistryTests
{
    private sealed class FakeProvider : IWebUIProvider
    {
        public string Id { get; init; } = "fake";
        public string DisplayName => Id;
        public IReadOnlyList<PageDefinition> Pages { get; init; } = new List<PageDefinition>();
    }

    private static PageDefinition Page(string route, bool showInNav = true, string? group = null) => new()
    {
        Route = route,
        Meta = new PageMeta { Title = route, ShowInNav = showInNav, Group = group },
        Cards = new List<CardDefinition>(),
        DataSources = new List<DataSourceDefinition>()
    };

    [Fact]
    public void FindPage_ExactRouteBeatsTemplate_AndNormalizesTrailingSlash()
    {
        var registry = new ProviderRegistry();
        registry.Register(new FakeProvider
        {
            Id = "p",
            Pages = new[]
            {
                Page("plugins/{name}"),
                Page("plugins/tools")
            }
        });

        Assert.Equal("plugins/tools", registry.FindPage("plugins/tools")!.Route);
        Assert.Equal("plugins/tools", registry.FindPage("/plugins/tools/")!.Route);
    }

    [Fact]
    public void FindPage_TemplateRanking_PrefersMoreLiteralSegments()
    {
        var registry = new ProviderRegistry();
        registry.Register(new FakeProvider
        {
            Id = "p",
            Pages = new[]
            {
                Page("a/{x}/c"),
                Page("a/b/{y}")
            }
        });

        var page = registry.FindPage("a/b/c");
        Assert.NotNull(page);
        Assert.Equal("a/b/{y}", page!.Route);
    }

    [Fact]
    public void FindPage_DecodesRouteParameter()
    {
        var registry = new ProviderRegistry();
        registry.Register(new FakeProvider
        {
            Id = "p",
            Pages = new[] { Page("plugins/{name}") }
        });

        var page = registry.FindPage("plugins/Plugin.File%20Tools", out var parameters);
        Assert.NotNull(page);
        Assert.Equal("Plugin.File Tools", parameters!["name"]);
    }

    [Fact]
    public void DuplicateProviderId_IsRejected()
    {
        var registry = new ProviderRegistry();
        Assert.True(registry.Register(new FakeProvider { Id = "same" }));
        Assert.False(registry.Register(new FakeProvider { Id = "same" }));
    }

    [Fact]
    public void FindNavGroupForPath_ResolvesHiddenDetailPrefix()
    {
        var registry = new ProviderRegistry();
        registry.Register(new FakeProvider
        {
            Id = "p",
            Pages = new[]
            {
                Page("sessions", group: "会话引擎"),
                Page("sessions/{sessionId}", showInNav: false, group: "会话引擎")
            }
        });

        Assert.Equal("会话引擎", registry.FindNavGroupForPath("sessions/abc123"));
        Assert.Equal("会话引擎", registry.FindNavGroupForPath("sessions"));
    }
}
