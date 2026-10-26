using AgentCoreProcessor.Engine.Graph;
using AgentLilara.GraphRuntime.Graph;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 兜底层级规则：只在 depth 0 触发一次；到顶只审计不派生；兜底自己失败也不再递归。
/// </summary>
public class GraphFallbackRunnerTests
{
    private static ActionIntent Send() => new(
        IntentKind.SendMessage,
        "1",
        new Dictionary<string, Cell> { ["text"] = Cell.Of("fallback") });

    [Fact]
    public async Task FirstFailureRunsExactlyOneFallback()
    {
        var evaluations = new List<int>();
        var executions = 0;

        var runner = new GraphFallbackRunner(
            evaluate: (depth, reasons) =>
            {
                evaluations.Add(depth);
                return Task.FromResult<IReadOnlyList<ActionIntent>>(new[] { Send() });
            },
            execute: _ =>
            {
                executions++;
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            });

        var result = await runner.RunAsync(new[] { "route-rejected:channel" });

        Assert.True(result.Attempted);
        Assert.Equal(1, result.Depth);
        Assert.Equal(new[] { 1 }, evaluations);
        Assert.Equal(1, executions);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task AtMaxDepthDoesNotDeriveAgain()
    {
        var evaluated = false;

        var runner = new GraphFallbackRunner(
            evaluate: (_, _) =>
            {
                evaluated = true;
                return Task.FromResult<IReadOnlyList<ActionIntent>>(new[] { Send() });
            },
            execute: _ => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));

        var failures = new[] { "send-failed:no-adapter" };
        var result = await runner.RunAsync(failures, depth: GraphFallbackRunner.MaxDepth);

        // 到顶：原失败原样带回，不再跑错误图
        Assert.False(result.Attempted);
        Assert.False(evaluated);
        Assert.Equal(failures, result.Failures);
    }

    [Fact]
    public async Task FallbackFailureIsReportedWithoutRecursing()
    {
        var evaluations = 0;

        var runner = new GraphFallbackRunner(
            evaluate: (_, _) =>
            {
                evaluations++;
                return Task.FromResult<IReadOnlyList<ActionIntent>>(new[] { Send() });
            },
            execute: _ => Task.FromResult<IReadOnlyList<string>>(new[] { "send-failed:still-broken" }));

        var result = await runner.RunAsync(new[] { "route-rejected:channel" });

        // 只跑一轮：兜底失败只上报，不引出第二轮
        Assert.Equal(1, evaluations);
        Assert.True(result.Attempted);
        Assert.Equal(new[] { "send-failed:still-broken" }, result.Failures);
    }

    [Fact]
    public async Task NoIntentsMeansNothingToExecute()
    {
        var executions = 0;

        var runner = new GraphFallbackRunner(
            evaluate: (_, _) => Task.FromResult<IReadOnlyList<ActionIntent>>(Array.Empty<ActionIntent>()),
            execute: _ =>
            {
                executions++;
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            });

        var result = await runner.RunAsync(new[] { "route-rejected:channel" });

        Assert.True(result.Attempted);
        Assert.Equal(0, executions);
        Assert.Empty(result.Failures);
    }
}
