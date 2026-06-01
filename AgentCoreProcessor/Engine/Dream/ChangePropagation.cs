using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// BFS 变更传播。节点 Importance/Certainty 变更沿边扩散，衰减到阈值停止。
    /// 本次只定义工具类，不接入调用链（后续做梦改造时接入）。
    /// </summary>
    internal static class ChangePropagation
    {
        public struct NodeChange
        {
            public int NodeId;
            public float DeltaImportance;
            public float DeltaCertainty;
        }

        public class PropagationResult
        {
            public List<NodeChange> Changes { get; init; } = new();
        }

        /// <summary>
        /// 从一个或多个种子节点沿边 BFS 传播变更。
        /// Δ_importance = Δ_self × edge.Relevance
        /// Δ_certainty  = Δ_self × edge.Relevance × edge.Support（support 符号决定方向）
        /// 停止条件: |Δ_i| + |Δ_c| &lt; epsilon
        /// BFS 保证最短路径优先，每个节点只访问一次。
        /// </summary>
        public static async Task<PropagationResult> PropagateAsync(
            List<NodeChange> seeds,
            float epsilon,
            MemoryRepository memories,
            MemoryLinkRepository links)
        {
            var pending = new Queue<NodeChange>();
            var visited = new HashSet<int>();
            var accumulated = new Dictionary<int, (float dImp, float dCert)>();

            foreach (var seed in seeds)
            {
                if (Math.Abs(seed.DeltaImportance) + Math.Abs(seed.DeltaCertainty) < epsilon)
                    continue;
                pending.Enqueue(seed);
                visited.Add(seed.NodeId);
            }

            while (pending.Count > 0)
            {
                var current = pending.Dequeue();

                if (Math.Abs(current.DeltaImportance) + Math.Abs(current.DeltaCertainty) < epsilon)
                    continue;

                var edges = await links.GetByMemoryIdAsync(current.NodeId);
                foreach (var edge in edges)
                {
                    int neighborId = edge.SourceId == current.NodeId ? edge.TargetId : edge.SourceId;
                    if (visited.Contains(neighborId)) continue;
                    visited.Add(neighborId);

                    float dImp = current.DeltaImportance * edge.Relevance;
                    float dCert = current.DeltaCertainty * edge.Relevance * edge.Support;

                    if (Math.Abs(dImp) + Math.Abs(dCert) < epsilon)
                        continue;

                    pending.Enqueue(new NodeChange
                    {
                        NodeId = neighborId,
                        DeltaImportance = dImp,
                        DeltaCertainty = dCert
                    });

                    if (accumulated.TryGetValue(neighborId, out var existing))
                        accumulated[neighborId] = (existing.dImp + dImp, existing.dCert + dCert);
                    else
                        accumulated[neighborId] = (dImp, dCert);
                }
            }

            return new PropagationResult
            {
                Changes = accumulated.Select(kv => new NodeChange
                {
                    NodeId = kv.Key,
                    DeltaImportance = kv.Value.dImp,
                    DeltaCertainty = kv.Value.dCert
                }).ToList()
            };
        }
    }
}
