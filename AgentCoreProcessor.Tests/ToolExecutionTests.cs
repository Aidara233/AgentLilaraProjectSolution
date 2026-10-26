using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Tool.Core;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// T1a：工具执行上下文。
/// <para>这是上一轮新增的横切机制：<c>ITool</c> 的签名里没有"谁在调用"，
/// 所以用 AsyncLocal 把会话与频道放进去。它一旦漏了清理，上下文就会串到别的会话——
/// 那种 bug 不会报错，只会让某个频道莫名其妙地收到别人的订阅。</para>
/// </summary>
public class ToolExecutionScopeTests
{
    [Fact]
    public void OutsideAnyScopeCurrentIsNull()
    {
        Assert.Null(ToolExecutionScope.Current);
    }

    [Fact]
    public void BeginExposesContext()
    {
        using var scope = ToolExecutionScope.Begin(new ToolExecutionContext
        {
            SessionId = "s1",
            SessionType = "express",
            ChannelId = 42,
        });

        Assert.Equal("s1", ToolExecutionScope.Current?.SessionId);
        Assert.Equal("express", ToolExecutionScope.Current?.SessionType);
        Assert.Equal(42, ToolExecutionScope.Current?.ChannelId);
    }

    [Fact]
    public void DisposeRestoresOuterState()
    {
        Assert.Null(ToolExecutionScope.Current);

        using (ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 1 }))
        {
            Assert.Equal(1, ToolExecutionScope.Current?.ChannelId);
        }

        Assert.Null(ToolExecutionScope.Current);
    }

    [Fact]
    public void NestedScopeRestoresOuter()
    {
        using var outer = ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 1 });

        using (ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 2 }))
        {
            Assert.Equal(2, ToolExecutionScope.Current?.ChannelId);
        }

        Assert.Equal(1, ToolExecutionScope.Current?.ChannelId);
    }

    [Fact]
    public async Task ConcurrentCallsDoNotLeakBetweenSessions()
    {
        // 并行任务各自看到自己的上下文——这正是 AsyncLocal 该有的性质，
        // 也是"一次投递跑到别的频道"这类事故的防线。
        var observed = await Task.WhenAll(
            Enumerable.Range(1, 24).Select(async channelId =>
            {
                using var scope = ToolExecutionScope.Begin(
                    new ToolExecutionContext { SessionId = $"s{channelId}", ChannelId = channelId });

                await Task.Yield();

                // 取不到就返回 -1：让"丢了上下文"表现为断言失败，而不是一个意外的 null
                return ToolExecutionScope.Current?.ChannelId ?? -1;
            }));

        Assert.Equal(Enumerable.Range(1, 24), observed);
    }
}

/// <summary>T1b：三个订阅工具的执行逻辑（此前只测了 Description）。</summary>
public class WatchToolTests
{
    [Fact]
    public async Task RejectsWhenSessionHasNoChannel()
    {
        var tool = new WatchTool(new TestToolContext(new RecordingWatchAccess()));

        // 不在作用域内 = 拿不到频道
        var result = await tool.ExecuteAsync(new List<string> { "mail" }, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Contains("频道", result.Error);
    }

    [Fact]
    public async Task RejectsWhenAccessServiceMissing()
    {
        var tool = new WatchTool(new TestToolContext(null));

        using var scope = ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 7 });
        var result = await tool.ExecuteAsync(new List<string> { "mail" }, CancellationToken.None);

        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task CreatesSubscriptionWithParsedArguments()
    {
        var access = new RecordingWatchAccess();
        var tool = new WatchTool(new TestToolContext(access));

        using var scope = ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 42 });

        var result = await tool.ExecuteAsync(
            new List<string> { "mail", "boss@corp", "盯着老板", "120", "3" },
            CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Equal(42, access.LastChannelId);

        var request = access.LastRequest!;
        Assert.Equal("mail", request.Source);
        Assert.Equal("boss@corp", request.From);
        Assert.Equal("盯着老板", request.Note);
        Assert.Equal(120, request.TtlMinutes);
        Assert.Equal(3, request.MaxFires);
    }

    [Fact]
    public async Task NonNumericLimitsFallBackToDefaults()
    {
        var access = new RecordingWatchAccess();
        var tool = new WatchTool(new TestToolContext(access));

        using var scope = ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 7 });

        await tool.ExecuteAsync(
            new List<string> { "mail", "", "", "abc", "-5" },
            CancellationToken.None);

        var request = access.LastRequest!;
        Assert.Equal(0, request.TtlMinutes);   // 非法 → 0 = 不过期
        Assert.Null(request.From);             // 空串归一成 null
        Assert.Null(request.Note);
    }

    [Fact]
    public async Task FailureFromAccessIsReported()
    {
        var access = new RecordingWatchAccess { FailWith = "没有信源「main」" };
        var tool = new WatchTool(new TestToolContext(access));

        using var scope = ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 7 });
        var result = await tool.ExecuteAsync(new List<string> { "main" }, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Contains("main", result.Error);
    }
}

/// <summary>T1c：取消与列举。</summary>
public class UnwatchAndListTests
{
    [Fact]
    public async Task UnwatchRequiresId()
    {
        var tool = new UnwatchTool(new TestToolContext(new RecordingWatchAccess()));

        using var scope = ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 7 });
        var result = await tool.ExecuteAsync(new List<string> { "  " }, CancellationToken.None);

        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task UnwatchReportsWhenSubscriptionNotFound()
    {
        var access = new RecordingWatchAccess { CancelResult = false };
        var tool = new UnwatchTool(new TestToolContext(access));

        using var scope = ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 7 });
        var result = await tool.ExecuteAsync(new List<string> { "nope" }, CancellationToken.None);

        Assert.Equal("failed", result.Status);
    }

    [Fact]
    public async Task UnwatchReportsSuccess()
    {
        var access = new RecordingWatchAccess { CancelResult = true };
        var tool = new UnwatchTool(new TestToolContext(access));

        using var scope = ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 7 });
        var result = await tool.ExecuteAsync(new List<string> { "abc123" }, CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Contains("abc123", result.Data?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task ListReportsEmptyState()
    {
        var tool = new ListWatchesTool(new TestToolContext(new RecordingWatchAccess()));

        using var scope = ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 7 });
        var result = await tool.ExecuteAsync(new List<string>(), CancellationToken.None);

        Assert.Equal("success", result.Status);
        Assert.Contains("没有", result.Data?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task ListJoinsMultipleSubscriptions()
    {
        var access = new RecordingWatchAccess
        {
            Infos =
            {
                new WatchInfo { Id = "w1", Source = "mail", Note = "等老板邮件", MaxFires = 1, FiredCount = 0 },
                new WatchInfo { Id = "w2", Source = "webhook", MaxFires = 5, FiredCount = 2 },
            },
        };

        var tool = new ListWatchesTool(new TestToolContext(access));

        using var scope = ToolExecutionScope.Begin(new ToolExecutionContext { ChannelId = 7 });
        var result = await tool.ExecuteAsync(new List<string>(), CancellationToken.None);

        var text = result.Data?.ToString() ?? string.Empty;
        Assert.Contains("w1", text);
        Assert.Contains("w2", text);
        Assert.Contains("等老板邮件", text);
    }
}

/// <summary>测试用的 <see cref="IToolContext"/>：只提供订阅服务，其余为 null。</summary>
internal sealed class TestToolContext : IToolContext
{
    private readonly IWatchAccess? _access;

    public TestToolContext(IWatchAccess? access) => _access = access;

    public T? GetService<T>() where T : class => _access as T;

    public IPluginStorage Storage => null!;

    public IWorkspaceAccess Workspace => null!;

    public IPluginDataAccess PluginData => null!;
}

/// <summary>记录调用参数的假订阅服务。</summary>
internal sealed class RecordingWatchAccess : IWatchAccess
{
    public int LastChannelId { get; private set; }

    public WatchRequest? LastRequest { get; private set; }

    public string? FailWith { get; init; }

    public bool CancelResult { get; init; } = true;

    public List<WatchInfo> Infos { get; } = new();

    public WatchResult Create(int channelId, WatchRequest request)
    {
        LastChannelId = channelId;
        LastRequest = request;

        return FailWith is null ? WatchResult.Ok("w-test") : WatchResult.Fail(FailWith);
    }

    public bool Cancel(int channelId, string watchId) => CancelResult;

    public IReadOnlyList<WatchInfo> List(int channelId) => Infos;

    public IReadOnlyList<SourceCapability> DescribeSources() => Array.Empty<SourceCapability>();
}
