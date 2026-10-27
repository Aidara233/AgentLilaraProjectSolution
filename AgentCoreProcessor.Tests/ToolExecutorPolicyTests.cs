using AgentCoreProcessor.Tool;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ToolExecutorPolicyTests
{
    private sealed class Tool(Func<CancellationToken, Task<ToolResult>> execute) : ITool
    {
        public string Name => "test";
        public string Description => "test";
        public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public TimeSpan Timeout => TimeSpan.FromMilliseconds(20);
        public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct) => execute(ct);
    }

    [Fact]
    public async Task ResolverThenDisabledThenPermissionOrderAndTextsStayStable()
    {
        var calls = new List<string>();
        var tool = new Tool(_ => { calls.Add("execute"); return Task.FromResult(new ToolResult { Status = "success" }); });
        var disabled = true;
        var executor = new ToolExecutor(name => { calls.Add("resolve"); return name == "missing" ? null : tool; },
            _ => { calls.Add("disabled"); return disabled; }, _ => null,
            (type, name) => { calls.Add("permission:" + type); return false; }) { PermissionGroup = "task" };
        var results = await executor.ExecuteAsync(new() { new() { Tool = "missing" }, new() { Tool = "test" } });
        Assert.Equal("未知工具: missing", results[0].Error);
        Assert.Equal("工具已禁用: 未知原因", results[1].Error);
        Assert.Equal(new[] { "resolve", "resolve", "disabled" }, calls);
        calls.Clear();
        disabled = false;
        results = await executor.ExecuteAsync(new() { new() { Tool = "test" } });
        Assert.Equal("工具「test」不在当前会话类型（task）允许范围内，不可用。请改用允许的工具。", results[0].Error);
        Assert.Equal(new[] { "resolve", "disabled", "permission:task" }, calls);
    }

    [Fact]
    public async Task AllowedCallsExposeScopeAndCallbacksRunSequentiallyAfterRestore()
    {
        var order = new List<string>();
        using var outer = ToolExecutionScope.Begin(new() { SessionId = "outer" });
        var tool = new Tool(async _ =>
        {
            await Task.Yield();
            Assert.Equal("session", ToolExecutionScope.Current!.SessionId);
            Assert.Equal(42, ToolExecutionScope.Current.ChannelId);
            order.Add("execute");
            return new ToolResult { Status = "success" };
        });
        var executor = new ToolExecutor(_ => tool, _ => false, _ => null, (_, _) => true)
            { SessionId = "session", ChannelId = 42, PermissionGroup = "channel" };
        executor.OnToolExecuted = (_, result) =>
        {
            Assert.Equal("outer", ToolExecutionScope.Current!.SessionId);
            Assert.Equal("success", result.Status);
            order.Add("callback");
            return Task.CompletedTask;
        };
        var results = await executor.ExecuteAsync(new() { new() { Tool = "test" }, new() { Tool = "test" } });
        Assert.Equal(2, results.Count);
        Assert.Equal(new[] { "execute", "callback", "execute", "callback" }, order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExceptionsAndTimeoutBecomeFailuresAndRestoreScope(bool timeout)
    {
        var previous = ToolExecutionScope.Current;
        var tool = new Tool(async ct =>
        {
            if (timeout) await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("tool failed");
        });
        var executor = new ToolExecutor(_ => tool, _ => false, _ => null, (_, _) => true);
        var callbacks = 0;
        executor.OnToolExecuted = (_, _) => { callbacks++; return Task.CompletedTask; };
        var result = Assert.Single(await executor.ExecuteAsync(new() { new() { Tool = "test" } }));
        Assert.Equal("failed", result.Status);
        Assert.Equal(timeout ? "执行超时（0.02s）" : "tool failed", result.Error);
        Assert.Equal(1, callbacks);
        Assert.Same(previous, ToolExecutionScope.Current);
    }
}
