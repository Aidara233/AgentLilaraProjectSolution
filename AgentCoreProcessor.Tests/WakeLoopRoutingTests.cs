// AgentCoreProcessor.Tests/WakeLoopRoutingTests.cs
using AgentCoreProcessor.Engine;
using Xunit;

namespace AgentCoreProcessor.Tests;

public class WakeLoopRoutingTests
{
    [Fact]
    public void WakeLoop_channel_wakes_session_when_channel_claimed()
    {
        var host = TestHost.BuildMasterEngineWithChannelClaim(channelId: 5, claimerSession: "S1");
        host.WakeLoop("channel:5");
        Assert.True(host.SessionWasWoken("S1"));
    }

    [Fact]
    public void WakeLoop_channel_falls_through_to_express_when_unclaimed()
    {
        var host = TestHost.BuildMasterEngineWithChannelClaim(channelId: 5, claimerSession: null);
        host.WakeLoop("channel:5");
        // 无 claim → Session 不应被唤醒
        Assert.False(host.SessionWasWoken("S1"));
    }

    [Fact]
    public void WakeLoop_channel_with_dead_claim_falls_through_to_express()
    {
        var host = TestHost.BuildMasterEngineWithChannelClaim(
            channelId: 5, claimerSession: "S1", sessionAlive: false);
        host.WakeLoop("channel:5");
        // 死亡 claim（会话已从 registry 消失）→ Session 不应被唤醒
        Assert.False(host.SessionWasWoken("S1"));
    }
}
