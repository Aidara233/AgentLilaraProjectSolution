using AgentCoreProcessor.Tool;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ToolExecutionScopeIsolationTests
{
    [Fact]
    public void NestedScopesRestorePreviousContextEvenWhenInnerThrows()
    {
        var before = ToolExecutionScope.Current;
        var outer = new ToolExecutionContext { SessionId = "outer", SessionType = "channel", ChannelId = 42 };
        using (ToolExecutionScope.Begin(outer))
        {
            Assert.Same(outer, ToolExecutionScope.Current);
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var inner = ToolExecutionScope.Begin(new() { SessionId = "inner", ChannelId = 99 });
                Assert.Equal("inner", ToolExecutionScope.Current!.SessionId);
                throw new InvalidOperationException("test");
            }));
            Assert.Same(outer, ToolExecutionScope.Current);
        }
        Assert.Same(before, ToolExecutionScope.Current);
    }

    [Fact]
    public async Task ConcurrentAsyncCallsKeepOwnContextAndRestoreInheritedScope()
    {
        var before = ToolExecutionScope.Current;
        var parent = new ToolExecutionContext { SessionId = "parent" };
        using (ToolExecutionScope.Begin(parent))
        {
            var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = 0;
            async Task Run(string id, int channel)
            {
                Assert.Same(parent, ToolExecutionScope.Current);
                using (ToolExecutionScope.Begin(new() { SessionId = id, SessionType = "task", ChannelId = channel }))
                {
                    if (Interlocked.Increment(ref entered) == 2) bothEntered.TrySetResult();
                    await bothEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.Yield();
                    Assert.Equal(id, ToolExecutionScope.Current!.SessionId);
                    Assert.Equal(channel, ToolExecutionScope.Current.ChannelId);
                    Assert.Equal("task", ToolExecutionScope.Current.SessionType);
                }
                Assert.Same(parent, ToolExecutionScope.Current);
            }
            await Task.WhenAll(Task.Run(() => Run("a", 1)), Task.Run(() => Run("b", 2)));
            Assert.Same(parent, ToolExecutionScope.Current);
        }
        Assert.Same(before, ToolExecutionScope.Current);
    }
}
