using AgentCoreProcessor.Engine.Graph;
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

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
