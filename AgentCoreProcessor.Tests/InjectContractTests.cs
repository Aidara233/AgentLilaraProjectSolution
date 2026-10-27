using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Tool.Core;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class InjectContractTests
{
    [Fact]
    public void LegacyHostNamesForwardToSdkTypes()
    {
        var host = typeof(WatchTool).Assembly;
        Assert.Equal("AgentLilara.PluginSDK", typeof(IInjectProvider).Assembly.GetName().Name);
        Assert.Equal(typeof(IInjectProvider).Assembly, typeof(InjectContext).Assembly);
        Assert.Contains(typeof(IInjectProvider), host.GetForwardedTypes());
        Assert.Contains(typeof(InjectContext), host.GetForwardedTypes());
        Assert.Same(typeof(IInjectProvider), host.GetType("AgentCoreProcessor.Engine.IInjectProvider", throwOnError: true));
        Assert.Same(typeof(InjectContext), host.GetType("AgentCoreProcessor.Engine.InjectContext", throwOnError: true));
    }
}
