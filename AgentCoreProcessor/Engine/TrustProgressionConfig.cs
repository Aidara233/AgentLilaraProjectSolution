using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    internal class TrustProgressionConfig
    {
        // 硬性条件阈值
        public int UnderstandingMemoryCount { get; set; } = 5;
        public int FamiliarityDays { get; set; } = 7;
        public int FamiliarityInteractionCount { get; set; } = 20;

        // TrustProgress 等级门槛（progress 低于此值时等级被压低）
        public float ProgressForWary { get; set; } = -15f;
        public float ProgressForHostile { get; set; } = -30f;

        // 自动增长
        public float DailyInteractionIncrement { get; set; } = 0.05f;
        public float DailyInteractionCap { get; set; } = 0.1f;

        // 做梦评估上限
        public float DreamEvaluationCap { get; set; } = 0.3f;

        // 警报冷却恢复天数
        public int AlertCooldownLevel1 { get; set; } = 1;
        public int AlertCooldownLevel2 { get; set; } = 3;
        public int AlertCooldownLevel3 { get; set; } = 7;
        public int AlertCooldownLevel4 { get; set; } = 14;

        public int GetAlertCooldownDays(int alertLevel) => alertLevel switch
        {
            1 => AlertCooldownLevel1,
            2 => AlertCooldownLevel2,
            3 => AlertCooldownLevel3,
            _ => AlertCooldownLevel4
        };

        public static TrustProgressionConfig Load(string path)
        {
            if (!File.Exists(path)) return new TrustProgressionConfig();
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<TrustProgressionConfig>(json)
                ?? new TrustProgressionConfig();
        }
    }
}
