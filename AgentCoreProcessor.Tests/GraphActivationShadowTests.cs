using System.Collections.Generic;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Messaging;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 影子判定测试：确认用图复算"要不要激活会话"的结果与旧逻辑一致。
/// 图来自 `Graphs/preset.activation.json`（测试输出目录里有一份拷贝）。
/// </summary>
public class GraphActivationShadowTests
{
    private static GraphActivationShadow NewShadow()
    {
        var shadow = GraphActivationShadow.TryCreate();
        Assert.NotNull(shadow);
        return shadow!;
    }

    private static Dictionary<string, IInputPort> Ports(
        out MessageInputPort normal,
        out MessageInputPort mention)
    {
        normal = new MessageInputPort(channelId: 1, subStream: "normal", weight: 5);
        mention = new MessageInputPort(channelId: 1, subStream: "mention", weight: 100);

        return new Dictionary<string, IInputPort>
        {
            ["ch1:normal"] = normal,
            ["ch1:mention"] = mention,
        };
    }

    [Fact]
    public void EqualDepthBatchesAndOversizedBatchResetCorrectly()
    {
        var shadow = NewShadow();
        var ports = Ports(out var normal, out _);
        foreach (var count in new[] { 25, 20, 20 })
        {
            for (var i = 0; i < count; i++) normal.Enqueue(new object());
            shadow.Observe(ports);
            Assert.True(shadow.GraphWantsActivation);
            normal.ResetQueueCount();
            shadow.Compare(true);
            Assert.True(shadow.LastComparison!.Matches);
            shadow.Synchronize(ports);
            Assert.False(shadow.GraphWantsActivation);
        }
        for (var i = 0; i < 19; i++) normal.Enqueue(new object());
        shadow.Observe(ports);
        Assert.False(shadow.GraphWantsActivation);
    }

    [Theory]
    [InlineData("system-input")]
    [InlineData("timeout")]
    [InlineData("wake-signal")]
    [InlineData("cancelled")]
    public void NonMessageCausesAreExplicitlyNotComparable(string reason)
    {
        var shadow = NewShadow();
        shadow.Compare(true, reason);
        Assert.False(shadow.LastComparison!.Comparable);
        Assert.Null(shadow.LastComparison.Matches);
        Assert.Equal(reason, shadow.LastComparison.Reason);
    }

    [Fact]
    public void NonDefaultWeightIsNotComparedAgainstTwentyMessagePreset()
    {
        var shadow = NewShadow();
        var port = new MessageInputPort(1, "normal", 10);
        var ports = new Dictionary<string, IInputPort> { ["normal"] = port };
        for (var i = 0; i < 10; i++) port.Enqueue(new object());
        shadow.Observe(ports);
        shadow.Compare(true);
        Assert.False(shadow.LastComparison!.Comparable);
        Assert.Equal("non-default-message-weight", shadow.LastComparison.Reason);
    }

    [Fact]
    public void GraphLoadsFromOutputDirectory()
    {
        Assert.NotNull(GraphActivationShadow.TryCreate());
    }

    [Fact]
    public void NineteenNormalMessagesDoNotActivate()
    {
        var shadow = NewShadow();
        var ports = Ports(out var normal, out _);

        for (var i = 0; i < 19; i++)
        {
            normal.Enqueue(new object());
        }

        shadow.Observe(ports);

        Assert.False(shadow.GraphWantsActivation);
    }

    [Fact]
    public void TwentyNormalMessagesActivate()
    {
        var shadow = NewShadow();
        var ports = Ports(out var normal, out _);

        for (var i = 0; i < 20; i++)
        {
            normal.Enqueue(new object());
        }

        shadow.Observe(ports);

        Assert.True(shadow.GraphWantsActivation);
    }

    [Fact]
    public void SingleMentionActivatesImmediately()
    {
        var shadow = NewShadow();
        var ports = Ports(out _, out var mention);

        mention.Enqueue(new object());
        shadow.Observe(ports);

        Assert.True(shadow.GraphWantsActivation);
    }

    [Fact]
    public void MentionResetsAccumulation()
    {
        var shadow = NewShadow();
        var ports = Ports(out var normal, out var mention);

        for (var i = 0; i < 19; i++)
        {
            normal.Enqueue(new object());
        }

        shadow.Observe(ports);
        Assert.False(shadow.GraphWantsActivation);

        // mention 立即激活
        mention.Enqueue(new object());
        shadow.Observe(ports);
        Assert.True(shadow.GraphWantsActivation);

        // 旧逻辑激活后清零计数；这里模拟 Compare 的窗口清理
        shadow.Compare(legacyActivated: true);
        Assert.False(shadow.GraphWantsActivation);

        // 队列计数被旧逻辑 ResetQueueCount 清零 → 再攒 19 条不应激活
        normal.ResetQueueCount();
        mention.ResetQueueCount();
        shadow.Observe(ports);

        for (var i = 0; i < 19; i++)
        {
            normal.Enqueue(new object());
        }

        shadow.Observe(ports);
        Assert.False(shadow.GraphWantsActivation);
    }
}
