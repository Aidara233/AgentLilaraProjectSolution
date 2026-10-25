using System;
using System.Collections.Generic;
using System.IO;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Logging;
using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using AgentLilara.GraphRuntime.Serialization;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine;

/// <summary>
/// **影子判定**：用接线图（<c>Graphs/preset.activation.json</c>）复算"要不要激活会话"，
/// 与旧逻辑比对、只在**不一致**时打日志。**不改变任何行为。**
///
/// 目的：把"要不要激活"这块从 `RuntimeSession` 的死代码搬到图之前，先用真实流量验证一致性。
/// 切换方式：一致率 100% 后把 `WaitForActivationAsync` 的返回值换成图结论。
/// </summary>
internal sealed class GraphActivationShadow
{
    private readonly GraphInstance _instance;

    /// <summary>每个消息端口上次看到的队列深度（用增量喂事件）。</summary>
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    /// <summary>自上次比对以来，图是否认为应该激活。</summary>
    private bool _graphFired;

    private GraphActivationShadow(GraphInstance instance)
    {
        _instance = instance;
    }

    /// <summary>加载失败返回 null —— 影子永远不该让主流程失败。</summary>
    public static GraphActivationShadow? TryCreate()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Graphs", "preset.activation.json");
            if (!File.Exists(path))
            {
                return null;
            }

            return FromJson(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "graph.shadow.load_failed", new { error = ex.Message });
            return null;
        }
    }

    /// <summary>直接用图 JSON 构造（测试用；加载失败会抛异常）。</summary>
    internal static GraphActivationShadow FromJson(string json) =>
        new(new GraphInstance(GraphLoader.Load(json)));

    /// <summary>图目前是否认为"该激活了"（自上次 Compare 以来）。</summary>
    internal bool GraphWantsActivation => _graphFired;

    /// <summary>把队列增量喂给图（每条积压消息合成一个事件；权重≥100 视为 @提及）。</summary>
    public void Observe(IReadOnlyDictionary<string, IInputPort> inputs)
    {
        foreach (var (name, port) in inputs)
        {
            if (port is not MessageInputPort mp)
            {
                continue;
            }

            var current = mp.QueueCount;
            var previous = _seen.TryGetValue(name, out var seen) ? seen : 0;
            var delta = current - previous;

            _seen[name] = current;
            if (delta <= 0)
            {
                continue;
            }

            for (var i = 0; i < delta; i++)
            {
                var evt = InboundEvent.Message(
                    adapterId: "shadow",
                    channelKey: mp.ChannelId.ToString(),
                    text: $"shadow-{name}-{i}",
                    channelKind: ChannelKind.Group,
                    mentionsBot: mp.Weight >= LegacyMentionWeight);

                _instance.Run(new EvalContext("shadow", i, evt));

                if (_instance.LastIntents.Count > 0)
                {
                    _graphFired = true;
                }
            }
        }
    }

    /// <summary>与旧判定比对。只在激活周期结束时调用一次。</summary>
    public void Compare(bool legacyActivated)
    {
        if (legacyActivated != _graphFired)
        {
            Signal.Warn(LogGroup.Engine, "graph.shadow.mismatch", new
            {
                legacyActivated,
                graphActivated = _graphFired,
                pending = _instance.ReadVariable("pending").ToString(),
            });
        }

        // 旧逻辑激活后会清零队列计数；图的变量已自行清零，这里只清比对窗口
        if (legacyActivated)
        {
            _graphFired = false;
        }
    }

    /// <summary>旧代码里 mention 端口的权重阈值（`MessageEngine.DeliverToSession` 用 100）。</summary>
    private const int LegacyMentionWeight = 100;
}
