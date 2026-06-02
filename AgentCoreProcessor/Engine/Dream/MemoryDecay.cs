using System;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 复利衰减计算。I(t) = I0 × (1 - rate)^Δdays。
    /// 由做梦巡逻和召回懒更新调用。
    /// </summary>
    internal static class MemoryDecay
    {
        /// <summary>每种类型的每日衰减率</summary>
        public static float GetDailyDecayRate(string type) => type switch
        {
            MemoryType.Knowledge  => 0.001f,  // 约700天腰斩
            MemoryType.Feedback   => 0.001f,
            MemoryType.Preference => 0.010f,  // 约70天腰斩
            MemoryType.Event      => 0.020f,  // 约35天腰斩
            MemoryType.Fact       => 0.045f,  // 约15天腰斩
            MemoryType.Inference  => 0.067f,  // 约10天腰斩
            MemoryType.State      => 0.370f,  // 约1.5天腰斩
            _ => 0.045f
        };

        /// <summary>
        /// 计算衰减后的重要性。I(t) = I0 × (1 - rate)^Δdays
        /// </summary>
        public static float ComputeDecayedImportance(
            float stored, DateTime lastTouched, DateTime now, string type)
        {
            if (stored <= 0f) return 0f;
            double days = (now - lastTouched).TotalDays;
            if (days <= 0) return stored;
            float rate = GetDailyDecayRate(type);
            float decayed = stored * MathF.Pow(1 - rate, (float)days);
            return Math.Clamp(decayed, 0f, 1f);
        }
    }
}
