using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 证据聚合纯函数（05-credibility-conflict-model.md 第四节）：
    /// - 证据强度 eᵢ = c_Sᵢ × c_Cᵢ × sᵢ（支持者实际可信度 × 支持边可信度 × 态度强度）
    /// - 组内聚合 noisy-OR：Support = 1 - ∏(1-eᵢ)，Oppose 同（质量 > 数量，单调不减）
    /// - 融合：L = logit(c⁰) + k_sup·Support - k_opp·Oppose；c_actual = σ(L)（sigmoid 天然有界 (0,1)）
    /// 纯函数，独立可单测。
    /// </summary>
    internal static class EvidenceMath
    {
        /// <summary>logit：概率 → log-odds。c 夹在 (0,1) 避免除零。</summary>
        public static double Logit(double c)
        {
            var x = Math.Clamp(c, 1e-9, 1 - 1e-9);
            return Math.Log(x / (1 - x));
        }

        /// <summary>sigmoid：log-odds → 概率。值域天然 (0,1)。</summary>
        public static double Sigmoid(double l) => 1.0 / (1.0 + Math.Exp(-l));

        /// <summary>单条证据强度：支持者实际可信度 × 支持边可信度 × 态度强度。</summary>
        public static double EvidenceStrength(double supporterCredibility, double edgeCredibility, double strength)
            => supporterCredibility * edgeCredibility * strength;

        /// <summary>组内 noisy-OR 聚合。质量 > 数量，单调不减。</summary>
        public static double NoisyOr(IEnumerable<double> strengths)
        {
            var product = 1.0;
            var any = false;
            foreach (var s in strengths)
            {
                if (s <= 0) continue;
                any = true;
                product *= (1.0 - Math.Clamp(s, 0, 1));
            }
            return any ? 1.0 - product : 0.0;
        }

        /// <summary>融合：logit 基础项 + 支持/反对线性项，sigmoid 有界。</summary>
        public static double Fuse(double anchor, double support, double oppose, double kSup, double kOpp)
        {
            var l = Logit(anchor) + kSup * support - kOpp * oppose;
            return Sigmoid(l);
        }

        /// <summary>阻尼迭代一步：c' = 0.85·c_new + 0.15·c_old（保证收敛）。</summary>
        public static double Dampen(double newValue, double oldValue, double damping = 0.85)
            => damping * newValue + (1 - damping) * oldValue;
    }
}
