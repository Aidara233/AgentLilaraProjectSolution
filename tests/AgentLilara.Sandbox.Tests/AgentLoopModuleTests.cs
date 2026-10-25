using AgentLilara.Kernel.Contracts;
using AgentLilara.Kernel.Graph;
using AgentLilara.Kernel.Modules;
using AgentLilara.Kernel.Serialization;
using AgentLilara.Modules.Agent;
using AgentLilara.Sandbox;
using AgentLilara.Sandbox.Agent;
using Xunit;

namespace AgentLilara.Sandbox.Tests;

/// <summary>
/// agent 模块接入图的端到端验证：清单 → 节点 → 调用 → 台账。
/// 用的是脚本化模型（<see cref="ScriptedAgentLoop"/>），不联网。
/// </summary>
public sealed class AgentLoopModuleTests
{
    private static (ModuleHost Host, ScriptedAgentLoop Agent) NewHost()
    {
        var host = new ModuleHost(ModuleCatalog.FromTypes(new[] { typeof(IAgentLoop) }));
        var agent = new ScriptedAgentLoop();
        host.Bind("agent.loop", agent);
        return (host, agent);
    }

    private static EvalContext Context(long seq, string message, bool mentionsBot = true) =>
        new($"trace-{seq}", seq, InboundEvent.Message("file", "chan-1", message, senderId: "u1", mentionsBot: mentionsBot));

    [Fact]
    public void ModuleContributesNodesWithCamelCasedNames()
    {
        var (host, _) = NewHost();
        var types = host.Types.ToList();

        Assert.Contains("module.agent.loop.run", types);
        Assert.Contains("module.agent.loop.cancel", types);
        Assert.Contains("read.agent.loop.state", types);
        Assert.Contains("read.agent.loop.activeSessions", types);
        Assert.Contains("read.agent.loop.toolBudget", types);
        Assert.Contains("write.agent.loop.toolBudget", types);

        // 生命周期方法不进画布；State 只读 → 没有写节点
        Assert.DoesNotContain("module.agent.loop.onMount", types);
        Assert.DoesNotContain("write.agent.loop.state", types);
    }

    [Fact]
    public void MessageGoesThroughGateIntoAgent_AndReplyIsRecorded()
    {
        var (host, agent) = NewHost();
        var graph = GraphLoader.Load(SampleGraphs.AgentCallJson, host.Registry);
        var instance = new GraphInstance(graph);

        var result = instance.Run(Context(1, "算 1+2"));

        Assert.True(result.Success, string.Join(",", result.Unresolved));

        // 模块真的被调用了，且工具循环在模块内部完成（图上只有一个方法节点）
        Assert.Contains("run file-session: 算 1+2", agent.Journal);

        // 模块方法节点是"即时"类：结果进台账，不产意图
        Assert.Empty(instance.LastIntents);

        var outcome = Assert.Single(instance.LastSinkOutcomes);
        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(0, agent.ActiveSessions);
        Assert.Equal("idle", agent.State);

        // Returns = "reply" → 整个回复对象落在唯一的口上
        var reply = Assert.IsType<AgentReply>(outcome.Outputs["reply"].Payload);
        Assert.Equal("= 3", reply.Text);
        Assert.Equal(2, reply.Rounds);
        var call = Assert.Single(reply.ToolCalls);
        Assert.Equal("add", call.Name);
        Assert.Equal("3", call.Result);
        Assert.False(call.IsError);
    }

    [Fact]
    public void GateBlocksAgent_WhenNotMentioned()
    {
        var (host, agent) = NewHost();
        var instance = new GraphInstance(GraphLoader.Load(SampleGraphs.AgentCallJson, host.Registry));

        Assert.True(instance.Run(Context(1, "闲聊", mentionsBot: false)).Success);

        Assert.Empty(agent.Journal);
        Assert.Empty(instance.LastSinkOutcomes);
    }

    [Fact]
    public void ToolBudgetPropertyChangesBehaviour()
    {
        var (host, agent) = NewHost();
        var instance = new GraphInstance(GraphLoader.Load(SampleGraphs.AgentCallJson, host.Registry));

        agent.ToolBudget = 0;
        Assert.True(instance.Run(Context(1, "算 1+2")).Success);

        var reply = Assert.IsType<AgentReply>(Assert.Single(instance.LastSinkOutcomes).Outputs["reply"].Payload);
        Assert.Equal("预算不足，未调用工具", reply.Text);
        Assert.Empty(reply.ToolCalls);
        Assert.Equal(1, reply.Rounds);
    }

    [Fact]
    public void PlainTextReplyUsesOneRound()
    {
        var (host, _) = NewHost();
        var instance = new GraphInstance(GraphLoader.Load(SampleGraphs.AgentCallJson, host.Registry));

        Assert.True(instance.Run(Context(1, "你好")).Success);

        var reply = Assert.IsType<AgentReply>(Assert.Single(instance.LastSinkOutcomes).Outputs["reply"].Payload);
        Assert.Equal("收到：你好", reply.Text);
        Assert.Empty(reply.ToolCalls);
        Assert.Equal(1, reply.Rounds);
    }

    [Fact]
    public void CancelMethodIsReachableFromGraph()
    {
        var host = new ModuleHost(ModuleCatalog.FromTypes(new[] { typeof(IAgentLoop) }));
        var agent = new ScriptedAgentLoop();
        host.Bind("agent.loop", agent);

        // cancel 的 sessionRef 是必需实参 → 必须接线
        const string cancelGraph = """
        {
          "classId": "channel.agent.cancel",
          "kind": "static",
          "version": 1,
          "nodes": [
            { "id": "on",      "type": "on.message" },
            { "id": "session", "type": "read.const", "params": { "value": "file-session" } },
            { "id": "cancel",  "type": "module.agent.loop.cancel" }
          ],
          "links": [
            { "from": "on.event",      "to": "cancel.event" },
            { "from": "session.value", "to": "cancel.sessionRef" }
          ]
        }
        """;

        var instance = new GraphInstance(GraphLoader.Load(cancelGraph, host.Registry));
        Assert.True(instance.Run(Context(1, "随便")).Success);

        Assert.Contains("cancel file-session: nothing running", agent.Journal);
        Assert.Equal("cancelled", agent.State);
    }
}
