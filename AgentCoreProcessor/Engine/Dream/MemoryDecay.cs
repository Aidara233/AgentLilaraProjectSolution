using System;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 半衰期衰减计算。不同记忆类型有不同的遗忘速度。
    /// 由做梦巡逻调用，不在 recall 中实时计算。
    /// </summary>
    internal static class MemoryDecay
    {
        /// <summary>每种类型的半衰期（天数）</summary>
        public static float GetHalfLife(string type) => type switch
        {
            MemoryType.Knowledge  => 700f,   // 约2年腰斩一次
            MemoryType.Feedback   => 700f,
            MemoryType.Preference => 70f,    // 2个多月不提开始缩水
            MemoryType.Event      => 35f,    // 一个月前的琐事不重要
            MemoryType.Fact       => 15f,    // 普通事实不提就贬值
            MemoryType.Inference  => 10f,    // 推论需重新验证
            MemoryType.State      => 1.5f,   // 时效性信息，两天不提大幅贬值
            _ => 15f
        };

        /// <summary>
        /// 计算衰减后的重要性。I(t) = I0 × 2^(-t / halfLife)
        /// </summary>
        /// <param name="stored">存储的重要性值</param>
        /// <param name="lastTouched">上次被巡逻或命中触碰的时间</param>
        /// <param name="now">当前时间</param>
        /// <param name="type">记忆类型</param>
        /// <returns>衰减后的重要性，范围 [0, 1]</returns>
        public static float ComputeDecayedImportance(
            float stored, DateTime lastTouched, DateTime now, string type)
        {
            if (stored <= 0f) return 0f;
            double days = (now - lastTouched).TotalDays;
            if (days <= 0) return stored;
            float halfLife = GetHalfLife(type);
            float decayed = stored * MathF.Pow(2f, -(float)(days / halfLife));
            return Math.Clamp(decayed, 0f, 1f);
        }
    }
}
