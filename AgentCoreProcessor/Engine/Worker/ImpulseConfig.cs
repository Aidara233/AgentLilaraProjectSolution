using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    internal class ImpulseConfig
    {
        // --- 累积（加法） ---
        public float BaseScore { get; set; } = 10f;
        public float AffinityBonusMax { get; set; } = 5f;
        public float MentionBonus { get; set; } = 30f;
        public float ParticipantDiscount2 { get; set; } = 2f;
        public float ParticipantDiscount3 { get; set; } = 4f;
        public float ParticipantDiscount4Plus { get; set; } = 6f;

        // --- 衰减（指数） ---
        public float DecayHalfLifeSeconds { get; set; } = 30f;

        // --- 阈值 ---
        public float Threshold { get; set; } = 100f;

        // --- 回复后 ---
        public float PostResponseCap { get; set; } = 20f;
        public float PostResponseCooldownSeconds { get; set; } = 3f;

        /// <summary>被@触发回复后额外扣减的冲动值，防止连续@连续触发。</summary>
        public float MentionPostResponseDeduction { get; set; } = 30f;

        // --- 闸门 ---
        public float BufferWindowSeconds { get; set; } = 3f;
        public float BufferMaxDelaySeconds { get; set; } = 10f;
        public float ColdTimeoutSeconds { get; set; } = 600f;

        public float GetParticipantDiscount(int participantCount) => participantCount switch
        {
            <= 1 => 0f,
            2 => ParticipantDiscount2,
            3 => ParticipantDiscount3,
            _ => ParticipantDiscount4Plus
        };

        public static ImpulseConfig Load(string path)
        {
            if (!File.Exists(path)) return new ImpulseConfig();
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<ImpulseConfig>(json) ?? new ImpulseConfig();
        }
    }
}
