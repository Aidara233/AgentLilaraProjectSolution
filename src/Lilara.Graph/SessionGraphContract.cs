using AgentLilara.GraphRuntime.Graph;

namespace AgentCoreProcessor.Engine.Graph;

/// <summary>会话消息图目前支持图变量与当前会话上下文注入。</summary>
internal static class SessionGraphContract
{
    public static void Validate(GraphClass graph)
    {
        if (graph.Nodes.Single(n => n.IsActivationSource) is not OnMessageEvent)
            throw new ArgumentException("session graph requires on.message");
        foreach (var sink in graph.Nodes.OfType<IntentSink>())
            if (sink.NodeType != "sink.inject")
                throw new ArgumentException("session graph supports only sink.inject actions; target must be the current sessionId");
    }
}
