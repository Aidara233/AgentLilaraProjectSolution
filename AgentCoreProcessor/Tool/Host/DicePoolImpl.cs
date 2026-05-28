using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK.Dice;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Tool.Host;

/// <summary>
/// 骰子系统实现。同时实现 IDiceRegistry（插件注册面）和 IDiceService（宿主摇结果）。
/// </summary>
internal class DicePoolImpl : IDiceRegistry, IDiceService
{
    private readonly ConcurrentDictionary<string, DiceFace> _faces = new();
    private readonly Random _rng = new();

    void IDiceRegistry.Register(DiceFace face)
    {
        _faces[face.Id] = face;
    }

    void IDiceRegistry.Unregister(string faceId)
    {
        _faces.TryRemove(faceId, out _);
    }

    IReadOnlyList<DiceFace> IDiceRegistry.GetAllFaces()
    {
        return _faces.Values.ToList().AsReadOnly();
    }

    async Task<IReadOnlyList<DiceResult>> IDiceService.RollAsync(int count, CancellationToken ct)
    {
        var faces = _faces.Values.ToList();
        if (faces.Count == 0)
            return Array.Empty<DiceResult>();

        // 按权重随机选 count 个面（允许重复）
        var selected = new List<DiceFace>(count);
        var totalWeight = faces.Sum(f => f.Weight);
        for (int i = 0; i < count; i++)
        {
            var roll = _rng.NextDouble() * totalWeight;
            double cumulative = 0;
            foreach (var face in faces)
            {
                cumulative += face.Weight;
                if (roll <= cumulative)
                {
                    selected.Add(face);
                    break;
                }
            }
        }

        // 并行摇结果
        var tasks = selected.Select(async face =>
        {
            try
            {
                return await face.Roll(ct);
            }
            catch (Exception)
            {
                return new DiceResult
                {
                    Meta = $"[error | {face.Category}]",
                    Content = $"({face.Label}: 摇结果失败)",
                    FollowUp = ""
                };
            }
        });
        return await Task.WhenAll(tasks);
    }
}
