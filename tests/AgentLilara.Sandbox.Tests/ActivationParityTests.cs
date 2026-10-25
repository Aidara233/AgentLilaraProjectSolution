using AgentLilara.Kernel.Contracts;
using AgentLilara.Kernel.Graph;
using AgentLilara.Sandbox;
using AgentLilara.Sandbox.Legacy;
using Xunit;

namespace AgentLilara.Sandbox.Tests;

/// <summary>
/// **新旧对照**：把旧代码里"要不要激活会话"的逻辑（<see cref="LegacyActivationPolicy"/>）
/// 与接线图 `preset.activation` 放在同一条消息序列上逐条比对。
/// 这是"拆一小块换成图，看能不能行"的验收。
/// </summary>
public sealed class ActivationParityTests
{
    private static InboundEvent Message(int index, bool mentioned) =>
        InboundEvent.Message(
            "file",
            "chan-1",
            mentioned ? $"@bot msg-{index}" : $"msg-{index}",
            ChannelKind.Group,
            senderId: $"u{index}",
            mentionsBot: mentioned);

    /// <summary>跑一条消息序列，返回每条消息两侧的判定（legacy 用旧规则，graph 跑图）。</summary>
    private static List<(int Index, bool Mentioned, bool Legacy, bool Graph)> RunSequence(int count, int mentionEvery)
    {
        var instance = new GraphInstance(SampleGraphs.LoadPresetActivation());
        var legacyPending = 0;
        var results = new List<(int, bool, bool, bool)>();

        for (var i = 1; i <= count; i++)
        {
            var mentioned = mentionEvery > 0 && i % mentionEvery == 0;

            // 旧规则：mention 走独立端口（不进 normal 累积），激活后两个端口都清零
            var legacyCounters = mentioned ? legacyPending : legacyPending + 1;
            var legacyHit = LegacyActivationPolicy.Decide(
                systemPortHasData: false,
                mentionQueueCount: mentioned ? 1 : 0,
                normalQueueCount: legacyCounters) != ActivationReason.None;

            legacyPending = legacyHit ? 0 : legacyCounters;

            // 新图：每条消息跑一次激活
            var context = new EvalContext($"trace-{i}", i, Message(i, mentioned));
            var result = instance.Run(context);
            Assert.True(result.Success, string.Join(",", result.Unresolved));

            var graphHit = instance.LastIntents.Count > 0;

            results.Add((i, mentioned, legacyHit, graphHit));
        }

        return results;
    }

    [Fact]
    public void GraphMatchesLegacy_OnMixedSequence()
    {
        var results = RunSequence(count: 45, mentionEvery: 17);

        foreach (var (index, mentioned, legacy, graph) in results)
        {
            Assert.True(
                legacy == graph,
                $"第 {index} 条（mention={mentioned}）不一致：legacy={legacy} graph={graph}");
        }

        // 第 17、34 条是 mention → 恰好两次激活；中间的普通消息都攒不够 20 条
        Assert.Equal(2, results.Count(r => r.Graph));
    }

    [Fact]
    public void AccumulatesTwentyNormalMessagesBeforeActivating()
    {
        var results = RunSequence(count: 25, mentionEvery: 0);

        // 第 20 条才达标，之后重新累积
        var hits = results.Where(r => r.Graph).Select(r => r.Index).ToList();
        Assert.Equal(new[] { 20 }, hits);
    }

    [Fact]
    public void MentionResetsAccumulation()
    {
        var instance = new GraphInstance(SampleGraphs.LoadPresetActivation());

        // 攒 19 条（差一条达标）
        for (var i = 1; i <= 19; i++)
        {
            instance.Run(new EvalContext($"t{i}", i, Message(i, mentioned: false)));
            Assert.Empty(instance.LastIntents);
        }

        Assert.Equal(19, instance.ReadVariable("pending").AsInt());

        // 一条 mention 立即激活，并把累积清零
        instance.Run(new EvalContext("t20", 20, Message(20, mentioned: true)));
        Assert.Single(instance.LastIntents);
        Assert.Equal(0, instance.ReadVariable("pending").AsInt());

        // 再攒 19 条仍然不触发 —— 证明重置生效（否则第 20 条会立刻触发）
        for (var i = 21; i <= 39; i++)
        {
            instance.Run(new EvalContext($"t{i}", i, Message(i, mentioned: false)));
            Assert.Empty(instance.LastIntents);
        }

        Assert.Equal(19, instance.ReadVariable("pending").AsInt());
    }

    [Fact]
    public void IntentCarriesMessageTextAndSession()
    {
        var instance = new GraphInstance(SampleGraphs.LoadPresetActivation());
        instance.Run(new EvalContext("t1", 1, Message(1, mentioned: true)));

        var intent = Assert.Single(instance.LastIntents);
        Assert.Equal(IntentKind.SessionPrompt, intent.Kind);
        Assert.Equal("default", intent.Target);
        Assert.Equal("@bot msg-1", intent.Args["text"].As<string>());
    }
}
