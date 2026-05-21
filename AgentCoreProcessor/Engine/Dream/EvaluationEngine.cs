using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 评价公式引擎。review_complete 时调用 ApplyAsync 批量应用缓冲评价。
    /// 公式: delta = (boundary - current) * rate * averaged_coefficient
    /// </summary>
    internal class EvaluationEngine
    {
        private readonly EvaluationScoreRepository _scores;
        private readonly ReviewConfig _cfg;

        private static readonly Dictionary<string, float> RatingCoefficients = new()
        {
            ["++"] = 1.0f,
            ["+"] = 0.4f,
            ["0"] = 0f,
            ["-"] = -0.4f,
            ["--"] = -1.0f
        };

        public EvaluationEngine(EvaluationScoreRepository scores, ReviewConfig cfg)
        {
            _scores = scores;
            _cfg = cfg;
        }

        /// <summary>
        /// 应用评价缓冲。按 (targetType, targetId, dimension) 分组取平均 coefficient，计算 delta。
        /// 返回应用的评价数量。
        /// </summary>
        public async Task<int> ApplyAsync(List<EvaluationBufferEntry> buffer)
        {
            if (buffer.Count == 0) return 0;

            var groups = buffer
                .GroupBy(e => (e.TargetType, e.TargetId, e.Dimension))
                .ToList();

            int applied = 0;

            foreach (var group in groups)
            {
                var (targetType, targetId, dimension) = group.Key;

                var coefficients = group
                    .Select(e => RatingCoefficients.GetValueOrDefault(e.Rating, 0f))
                    .ToList();
                var avgCoefficient = coefficients.Average();

                if (Math.Abs(avgCoefficient) < 0.001f)
                {
                    // 纯 0 评价：只更新 LastEvaluatedAt
                    var existing = await _scores.GetAsync(targetType, targetId, dimension);
                    if (existing != null)
                    {
                        existing.LastEvaluatedAt = DateTime.Now;
                        await _scores.UpsertAsync(existing);
                    }
                    else
                    {
                        await _scores.UpsertAsync(new EvaluationScore
                        {
                            TargetType = targetType,
                            TargetId = targetId,
                            Dimension = dimension,
                            Value = GetBaseline(targetType),
                            LastEvaluatedAt = DateTime.Now
                        });
                    }
                    applied++;
                    continue;
                }

                var current = await GetCurrentValue(targetType, targetId, dimension);
                var boundary = avgCoefficient > 0 ? GetCeiling(targetType) : GetFloor(targetType);
                var delta = (boundary - current) * _cfg.EvaluationRate * avgCoefficient;
                var newValue = current + delta;

                // 钳位
                newValue = Math.Clamp(newValue, GetFloor(targetType), GetCeiling(targetType));

                await _scores.UpsertAsync(new EvaluationScore
                {
                    TargetType = targetType,
                    TargetId = targetId,
                    Dimension = dimension,
                    Value = newValue,
                    LastEvaluatedAt = DateTime.Now
                });

                Signal.Event(LogGroup.Engine, "评价应用", new
                {
                    targetType, targetId, dimension,
                    avgCoefficient = avgCoefficient,
                    current, delta, newValue,
                    ratingCount = coefficients.Count
                });

                applied++;
            }

            return applied;
        }

        private async Task<float> GetCurrentValue(string targetType, int targetId, string dimension)
        {
            var score = await _scores.GetAsync(targetType, targetId, dimension);
            return score?.Value ?? GetBaseline(targetType);
        }

        private float GetBaseline(string targetType)
            => targetType == "channel" ? _cfg.ChannelBaseline : _cfg.PersonBaseline;

        private float GetCeiling(string targetType)
            => targetType == "channel" ? _cfg.ChannelCeiling : _cfg.PersonCeiling;

        private float GetFloor(string targetType)
            => targetType == "channel" ? _cfg.ChannelFloor : _cfg.PersonFloor;
    }
}
