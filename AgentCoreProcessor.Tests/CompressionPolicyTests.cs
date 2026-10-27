using AgentCoreProcessor.Engine.Modules;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class CompressionPolicyTests
{
    [Theory]
    [InlineData(4999, (int)CompressionTier.None)]
    [InlineData(5000, (int)CompressionTier.None)]
    [InlineData(79999, (int)CompressionTier.None)]
    [InlineData(80000, (int)CompressionTier.L1)]
    [InlineData(129999, (int)CompressionTier.L1)]
    [InlineData(130000, (int)CompressionTier.L2)]
    [InlineData(149999, (int)CompressionTier.L2)]
    [InlineData(150000, (int)CompressionTier.L3)]
    public void DefaultThresholdBoundaries(int tokens, int expected)
    {
        var policy = new CompressionPolicy(new(5000, 80000, 130000, 150000), () => { });
        Assert.Equal((CompressionTier)expected, policy.Evaluate(tokens));
    }

    [Fact]
    public void MinimumPrecedesAllTiersAndL3PrecedesL2AndOverride()
    {
        var policy = new CompressionPolicy(new(100, 10, 20, 30), () => { }, 1);
        Assert.Equal(CompressionTier.None, policy.Evaluate(99));
        Assert.Equal(CompressionTier.L3, policy.Evaluate(100));
        var overridden = new CompressionPolicy(new(5, 80, 130, 150), () => { }, 20);
        Assert.Equal(CompressionTier.None, overridden.Evaluate(19));
        Assert.Equal(CompressionTier.L1, overridden.Evaluate(20));
        Assert.Equal(CompressionTier.L2, overridden.Evaluate(130));
    }

    [Fact]
    public void L1ReminderStaysUntilMarkedAndDoesNotReturnAfterTierChanges()
    {
        var policy = new CompressionPolicy(new(5, 80, 130, 150), () => { });
        const string reminder = "[系统提示] 上下文稍大。如果当前内容不重要，可以考虑调用 compact 工具来精简会话。\n调用时需提供一段摘要作为参数，描述：做了什么、得出了什么、下一步要做什么。";
        Assert.Equal(reminder, policy.GetInjectText(80));
        Assert.Equal(reminder, policy.GetInjectText(80));
        policy.MarkL1Injected();
        Assert.Null(policy.GetInjectText(80));
        Assert.Equal("[系统警告] 会话长度已经较高，建议尽快在合适的地方调用 compact 工具。\ncompact 参数应是你对当前会话的完整总结。调用后会启动新会话承接你的摘要。", policy.GetInjectText(130));
        Assert.Equal("[系统强制] 上下文已达硬上限，本轮请调用 compact 工具结束当前会话并切换到新会话。\n参数是你对会话的完整总结。请认真总结，新会话将基于你的总结继续工作。", policy.GetInjectText(150));
        policy.Evaluate(0);
        Assert.Null(policy.GetInjectText(80));
    }

    [Fact]
    public void L3CallbackFiresOnEntryAndReentryOnly()
    {
        var callbacks = 0;
        var policy = new CompressionPolicy(new(5, 80, 130, 150), () => callbacks++);
        policy.Evaluate(150);
        policy.GetInjectText(160);
        Assert.Equal(1, callbacks);
        policy.Evaluate(130);
        policy.GetInjectText(150);
        Assert.Equal(2, callbacks);
    }
}
