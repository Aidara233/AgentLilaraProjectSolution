using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Source;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// P4：模型侧订阅工具的实现面（创建 / 取消 / 列举）。
/// <para>验的是两条安全边界：没有频道不能建订阅；只能取消自己频道的订阅。</para>
/// </summary>
public class WatchAccessTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "lilara-watch-" + Guid.NewGuid().ToString("N"));

    public WatchAccessTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Graphs"));
    }

    [Fact]
    public void CreateStoresSubscriptionForChannel()
    {
        var access = new WatchAccessImpl(GraphManager.Open(_root, active: true));

        var result = access.Create(42, new WatchRequest
        {
            Source = "mail",
            From = "boss@corp",
            Note = "盯着老板的邮件",
            TtlMinutes = 30,
            MaxFires = 1,
        });

        Assert.True(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.WatchId));

        var listed = Assert.Single(access.List(42));
        Assert.Equal("mail", listed.Source);
        Assert.Equal("盯着老板的邮件", listed.Note);
        Assert.NotNull(listed.ExpiresAt);
    }

    [Fact]
    public void CreateWithoutChannelFails()
    {
        var access = new WatchAccessImpl(GraphManager.Open(_root, active: true));

        var result = access.Create(0, new WatchRequest { Source = "mail" });

        Assert.False(result.Success);
        Assert.Contains("频道", result.Error);
    }

    [Fact]
    public void CreateWithoutSourceFails()
    {
        var access = new WatchAccessImpl(GraphManager.Open(_root, active: true));

        var result = access.Create(42, new WatchRequest { Source = " " });

        Assert.False(result.Success);
    }

    [Fact]
    public void CancelOnlyWorksForOwnChannel()
    {
        var access = new WatchAccessImpl(GraphManager.Open(_root, active: true));
        var created = access.Create(42, new WatchRequest { Source = "mail", From = "boss@corp" });

        Assert.False(access.Cancel(43, created.WatchId!)); // 别的频道拆不掉
        Assert.Single(access.List(42));

        Assert.True(access.Cancel(42, created.WatchId!));
        Assert.Empty(access.List(42));
    }

    [Fact]
    public void ListIsScopedPerChannel()
    {
        var access = new WatchAccessImpl(GraphManager.Open(_root, active: true));
        access.Create(42, new WatchRequest { Source = "mail" });
        access.Create(43, new WatchRequest { Source = "mail" });

        Assert.Single(access.List(42));
        Assert.Single(access.List(43));
    }

    // ── F2：信源与字段校验 ──

    [Fact]
    public void CreateRejectsUnknownSource()
    {
        var access = new WatchAccessImpl(GraphManager.Open(_root, active: true), SourcesWith("mail", "from"));

        var result = access.Create(42, new WatchRequest { Source = "main", From = "x@y" });

        // 受理一个不存在信源的订阅 = 登记一条永远不会命中的规则
        Assert.False(result.Success);
        Assert.Contains("mail", result.Error); // 错误里要告知可用清单
    }

    [Fact]
    public void CreateRejectsFieldTheSourceDoesNotProvide()
    {
        var access = new WatchAccessImpl(GraphManager.Open(_root, active: true), SourcesWith("mail", "subject"));

        var result = access.Create(42, new WatchRequest { Source = "mail", From = "x@y" });

        Assert.False(result.Success);
        Assert.Contains("from", result.Error);
        Assert.Contains("subject", result.Error); // 顺带告诉它支持什么
    }

    [Fact]
    public void CreateAcceptsDeclaredField()
    {
        var access = new WatchAccessImpl(
            GraphManager.Open(_root, active: true), SourcesWith("mail", "from", "subject"));

        var result = access.Create(42, new WatchRequest { Source = "mail", From = "x@y" });

        Assert.True(result.Success);
    }

    [Fact]
    public void SourceWithoutDeclaredFieldsSkipsKeyValidation()
    {
        // 不声明字段 = 不校验：向后兼容还没跟上这套约定的插件
        var access = new WatchAccessImpl(GraphManager.Open(_root, active: true), SourcesWith("legacy"));

        Assert.True(access.Create(42, new WatchRequest { Source = "legacy", From = "x@y" }).Success);
    }

    [Fact]
    public void DescribeSourcesListsWhatEachSourceProvides()
    {
        var access = new WatchAccessImpl(
            GraphManager.Open(_root, active: true), SourcesWith("mail", "from", "subject"));

        var summary = Assert.Single(access.DescribeSources());
        Assert.Equal("mail", summary.Kind);
        Assert.Contains("from", summary.Fields);
    }

    // ── F3：工具描述要带上真实存在的信源 ──

    [Fact]
    public void WatchToolDescriptionListsAvailableSources()
    {
        var access = new WatchAccessImpl(
            GraphManager.Open(_root, active: true), SourcesWith("mail", "from", "subject"));
        var tool = new AgentCoreProcessor.Tool.Core.WatchTool(new FakeToolContext(access));

        // 模型看到的是真实清单，而不是靠猜 source 该填什么
        Assert.Contains("mail", tool.Description);
        Assert.Contains("from", tool.Description);
    }

    [Fact]
    public void WatchToolDescriptionStaysUsableWithoutSources()
    {
        var access = new WatchAccessImpl(GraphManager.Open(_root, active: true), new SourceManager());
        var tool = new AgentCoreProcessor.Tool.Core.WatchTool(new FakeToolContext(access));

        Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        Assert.DoesNotContain("可用信源", tool.Description);
    }

    private sealed class FakeToolContext : AgentLilara.PluginSDK.IToolContext
    {
        private readonly IWatchAccess _access;

        public FakeToolContext(IWatchAccess access) => _access = access;

        public T? GetService<T>() where T : class => _access as T;

        public AgentLilara.PluginSDK.IPluginStorage Storage => null!;

        public AgentLilara.PluginSDK.IWorkspaceAccess Workspace => null!;

        public AgentLilara.PluginSDK.IPluginDataAccess PluginData => null!;
    }

    private static SourceManager SourcesWith(string kind, params string[] fields)
    {
        var manager = new SourceManager();
        manager.Register(new InjectedEventSource(kind, kind, fields));
        return manager;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
