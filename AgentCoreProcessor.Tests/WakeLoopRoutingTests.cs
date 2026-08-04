// AgentCoreProcessor.Tests/WakeLoopRoutingTests.cs
using AgentCoreProcessor.Engine;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// WakeLoop 测试。LoopId 前缀概念移除后，WakeLoop 只接受裸 sessionId 并唤醒对应会话
/// （经 SessionEngine 池解析，跨 compact forward）。
/// </summary>
public class WakeLoopRoutingTests
{
    [Fact]
    public void WakeLoop_wakes_session_by_bare_sessionId()
    {
        var host = TestHost.BuildMasterEngineWithChannelClaim(channelId: 5, claimerSession: "S1");
        host.WakeLoop("S1");
        Assert.True(host.SessionWasWoken("S1"));
    }

    [Fact]
    public void WakeLoop_unknown_session_is_noop()
    {
        var host = TestHost.BuildMasterEngineWithChannelClaim(channelId: 5, claimerSession: null);
        host.WakeLoop("SX");
        Assert.False(host.SessionWasWoken("S1"));
    }
}
