using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 情绪服务。五轴情绪模型（02-emotion-model.md）：
    /// - 连续值 [-1, +1]，五根轴（兴奋/喜悦/敏觉/满足/积极）
    /// - 运动公式 new = current + (target - current) × rate
    /// - 事件提交目标值（吸引子），不是增量
    /// - 内部锁串行 ApplyAsync，避免并发覆盖
    /// - 实时应用：读最新 → 算 → 写内存 → 快照落库 → EmotionState.json 原子写
    /// - 重启：EmotionState.json 优先，缺失/损坏从 EmotionSnapshot 恢复
    /// 唯一情绪写入者，由 MindEngine 持有。
    /// </summary>
    internal class EmotionService
    {
        private readonly object _lock = new();

        // 五轴当前值
        private float _excitement;
        private float _joy;
        private float _alertness;
        private float _satisfaction;
        private float _optimism;
        private DateTime _lastUpdatedAt = DateTime.Now;

        private readonly EmotionSnapshotRepository _snapshots;
        private readonly string _statePath;

        /// <summary>每轴热容（低 = 一推就动，高 = 稳得住）。数值待定见 06。</summary>
        private static readonly Dictionary<string, float> HeatCapacity = new()
        {
            { "excitement", 1.0f },
            { "joy", 0.7f },
            { "alertness", 0.5f },
            { "satisfaction", 0.5f },
            { "optimism", 0.3f },
        };

        public EmotionService(EmotionSnapshotRepository snapshots)
        {
            _snapshots = snapshots;
            _statePath = Path.Combine(PathConfig.StoragePath, "Emotion", "EmotionState.json");
            Restore();
        }

        // ---- 读取 ----

        public Task<EmotionStateDto> GetCurrentAsync()
        {
            lock (_lock)
            {
                return Task.FromResult(new EmotionStateDto
                {
                    Excitement = _excitement,
                    Joy = _joy,
                    Alertness = _alertness,
                    Satisfaction = _satisfaction,
                    Optimism = _optimism,
                    LastUpdatedAt = _lastUpdatedAt,
                });
            }
        }

        public async Task<List<EmotionSnapshotDto>> GetRecentSnapshotAsync(int limit = 20)
        {
            var snaps = await _snapshots.GetRecentAsync(limit);
            var result = new List<EmotionSnapshotDto>();
            foreach (var s in snaps)
            {
                result.Add(new EmotionSnapshotDto
                {
                    Excitement = s.Excitement,
                    Joy = s.Joy,
                    Alertness = s.Alertness,
                    Satisfaction = s.Satisfaction,
                    Optimism = s.Optimism,
                    SourceRef = s.SourceRef,
                    CreatedAt = s.CreatedAt,
                });
            }
            return result;
        }

        // ---- 写入（唯一入口，锁串行） ----

        /// <summary>应用一条情绪信号。目标值吸引子，实时计算。快照随落库。</summary>
        public async Task ApplyAsync(EmotionSignal signal)
        {
            if (signal == null || string.IsNullOrEmpty(signal.Axis)) return;

            EmotionSnapshot snapshot;
            lock (_lock)
            {
                float target = Math.Clamp(signal.Target, -1f, 1f);
                float rate = GetRate(signal.Axis);
                float delta;

                switch (signal.Axis)
                {
                    case "excitement": delta = (target - _excitement) * rate; _excitement += delta; break;
                    case "joy": delta = (target - _joy) * rate; _joy += delta; break;
                    case "alertness": delta = (target - _alertness) * rate; _alertness += delta; break;
                    case "satisfaction": delta = (target - _satisfaction) * rate; _satisfaction += delta; break;
                    case "optimism": delta = (target - _optimism) * rate; _optimism += delta; break;
                    default: return; // 未知轴忽略
                }

                _lastUpdatedAt = DateTime.Now;

                snapshot = new EmotionSnapshot
                {
                    Excitement = _excitement,
                    Joy = _joy,
                    Alertness = _alertness,
                    Satisfaction = _satisfaction,
                    Optimism = _optimism,
                    SourceRef = signal.Reason,
                };
            }

            // 快照落库 + json 原子写（锁外做 IO）
            await _snapshots.CreateAsync(snapshot);
            SaveState();
        }

        /// <summary>批量应用（Writer 原子批次内，一次收集全部 journal 信号）。</summary>
        public async Task ApplyBatchAsync(IEnumerable<EmotionSignal> signals)
        {
            foreach (var s in signals)
                await ApplyAsync(s);
        }

        // ---- 内部 ----

        /// <summary>由热容得 rate（低热容 rate 高）。</summary>
        private static float GetRate(string axis)
        {
            return HeatCapacity.TryGetValue(axis, out var cap) ? cap : 0.5f;
        }

        /// <summary>EmotionState.json 原子写。</summary>
        private void SaveState()
        {
            var cfg = new EmotionStateConfig
            {
                FormatVersion = 1,
                Excitement = _excitement,
                Joy = _joy,
                Alertness = _alertness,
                Satisfaction = _satisfaction,
                Optimism = _optimism,
                LastUpdatedAt = _lastUpdatedAt,
            };
            cfg.Save(_statePath);
        }

        /// <summary>启动恢复：EmotionState.json 优先，缺失/损坏从最近快照恢复。</summary>
        private void Restore()
        {
            var cfg = EmotionStateConfig.Load(_statePath);
            if (cfg.FormatVersion >= 1)
            {
                _excitement = cfg.Excitement;
                _joy = cfg.Joy;
                _alertness = cfg.Alertness;
                _satisfaction = cfg.Satisfaction;
                _optimism = cfg.Optimism;
                _lastUpdatedAt = cfg.LastUpdatedAt;
            }
            else
            {
                // 兜底：从最近快照恢复
                var latest = _snapshots.GetLatestAsync().GetAwaiter().GetResult();
                if (latest != null)
                {
                    _excitement = latest.Excitement;
                    _joy = latest.Joy;
                    _alertness = latest.Alertness;
                    _satisfaction = latest.Satisfaction;
                    _optimism = latest.Optimism;
                    _lastUpdatedAt = latest.CreatedAt;
                }
            }
        }
    }
}
