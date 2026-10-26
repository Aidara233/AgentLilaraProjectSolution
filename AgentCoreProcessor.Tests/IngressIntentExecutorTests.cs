using AgentCoreProcessor.Engine.Graph;
using AgentLilara.GraphRuntime.Graph;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 意图执行器的骨架规则：同一消息只投递一次；失败不占键（留给兜底重试）；
/// 发送失败单独上报；不认识的动作静默跳过。
/// </summary>
public class IngressIntentExecutorTests
{
    private static ActionIntent Route(string sessionType = "channel") => new(
        IntentKind.SessionRoute,
        sessionType,
        new Dictionary<string, Cell> { ["sessionType"] = Cell.Of(sessionType) });

    private static ActionIntent Send() => new(
        IntentKind.SendMessage,
        "1",
        new Dictionary<string, Cell> { ["text"] = Cell.Of("hi") });

    private static ActionIntent Prompt() => new(
        IntentKind.SessionPrompt,
        "default",
        new Dictionary<string, Cell>
        {
            ["sessionRef"] = Cell.Of("default"),
            ["text"] = Cell.Of("hi"),
        });

    private static (IngressIntentExecutor Executor, HashSet<string> Keys, List<string> Routes, List<int> Defaults, List<string> Sends)
        Build(Func<string, Task<string?>>? route = null,
              Func<Task<string?>>? deliverDefault = null,
              Func<ActionIntent, Task<string?>>? send = null)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var routes = new List<string>();
        var defaults = new List<int>();
        var sends = new List<string>();

        var executor = new IngressIntentExecutor(
            keys,
            deliveryKey: "1:42",
            deliverDefault: async () =>
            {
                defaults.Add(1);
                return deliverDefault is null ? null : await deliverDefault();
            },
            route: async sessionType =>
            {
                routes.Add(sessionType);
                return route is null ? null : await route(sessionType);
            },
            send: async intent =>
            {
                sends.Add(intent.Target);
                return send is null ? null : await send(intent);
            });

        return (executor, keys, routes, defaults, sends);
    }

    [Fact]
    public async Task TwoDeliveryIntentsDeliverOnce()
    {
        var (executor, keys, routes, _, _) = Build();

        var failures = await executor.ExecuteAsync(new[] { Route("channel"), Route("task") });

        Assert.Empty(failures);
        Assert.Single(routes);
        Assert.Equal("channel", routes[0]);
        Assert.Contains("1:42", keys);
        Assert.True(executor.AlreadyDelivered);
    }

    [Fact]
    public async Task FailedRouteLeavesKeyOpenForRetry()
    {
        var attempts = 0;
        var (executor, keys, routes, _, _) = Build(route: _ =>
        {
            attempts++;
            return Task.FromResult(attempts == 1 ? "route-rejected:channel" : null);
        });

        var failures = await executor.ExecuteAsync(new[] { Route("channel"), Route("channel") });

        // 第一次失败被上报，但没占键；第二次真正重试成功
        Assert.Single(failures);
        Assert.Equal("route-rejected:channel", failures[0]);
        Assert.Equal(2, routes.Count);
        Assert.Contains("1:42", keys);
    }

    [Fact]
    public async Task SuccessBlocksLaterPromptDelivery()
    {
        var (executor, _, _, defaults, _) = Build();

        var failures = await executor.ExecuteAsync(new[] { Route("channel"), Prompt() });

        Assert.Empty(failures);
        Assert.Empty(defaults);
        Assert.True(executor.AlreadyDelivered);
    }

    [Fact]
    public async Task PromptWithoutRouteUsesDefaultDelivery()
    {
        var (executor, keys, _, defaults, _) = Build();

        var failures = await executor.ExecuteAsync(new[] { Prompt() });

        Assert.Empty(failures);
        Assert.Single(defaults);
        Assert.Contains("1:42", keys);
    }

    [Fact]
    public async Task SendFailureIsReportedButDoesNotConsumeDeliveryKey()
    {
        var (executor, keys, _, _, sends) = Build(send: _ => Task.FromResult<string?>("send-failed:no-adapter"));

        var failures = await executor.ExecuteAsync(new[] { Send() });

        Assert.Single(failures);
        Assert.Equal("send-failed:no-adapter", failures[0]);
        Assert.Single(sends);
        Assert.DoesNotContain("1:42", keys);
    }

    [Fact]
    public async Task UnsupportedIntentIsIgnored()
    {
        var (executor, keys, routes, defaults, sends) = Build();
        var inject = new ActionIntent(IntentKind.SessionInject, "default",
            new Dictionary<string, Cell> { ["text"] = Cell.Of("hi") });

        var failures = await executor.ExecuteAsync(new[] { inject });

        Assert.Empty(failures);
        Assert.Empty(routes);
        Assert.Empty(defaults);
        Assert.Empty(sends);
        Assert.False(executor.AlreadyDelivered);
        Assert.Empty(keys);
    }
}
