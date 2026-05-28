using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK.Dice;

namespace AgentLilara.PluginSDK.Services;

/// <summary>
/// 骰子摇奖服务。SystemEngine 闲时调用 RollAsync 批量摇 N 个面。
/// </summary>
public interface IDiceService
{
    /// <summary>随机挑选 count 个面（按权重），并行摇出结果。</summary>
    Task<IReadOnlyList<DiceResult>> RollAsync(int count, CancellationToken ct = default);
}
