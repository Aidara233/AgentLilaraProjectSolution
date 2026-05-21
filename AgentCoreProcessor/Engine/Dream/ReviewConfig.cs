using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    internal class ReviewConfig
    {
        // ---- 评价参数 ----

        public float EvaluationRate { get; set; } = 0.05f;

        public float PersonFloor { get; set; } = -50f;
        public float PersonCeiling { get; set; } = 50f;
        public float PersonBaseline { get; set; } = 0f;

        public float ChannelFloor { get; set; } = 0.1f;
        public float ChannelCeiling { get; set; } = 3.0f;
        public float ChannelBaseline { get; set; } = 1.0f;

        // ---- 预算 ----

        public int TokenBudget { get; set; } = 50000;
        public int ReserveBudget { get; set; } = 15000;
        public int CompressionThreshold { get; set; } = 30000;

        // ---- 空转检测 ----

        public int MaxNavigationRounds { get; set; } = 3;

        // ---- 信任升级门槛 ----

        // Unknown → Stranger
        public int StrangerMinMessages { get; set; } = 3;

        // Stranger → Understanding
        public int UnderstandingMinMemories { get; set; } = 5;
        public int UnderstandingMinDays { get; set; } = 3;
        public float UnderstandingAnyDimension { get; set; } = 8f;

        // Understanding → Familiarity
        public int FamiliarityMinDays { get; set; } = 14;
        public float FamiliarityMajorityDimension { get; set; } = 20f;

        // Familiarity → Trust
        public int TrustMinDays { get; set; } = 30;
        public int TrustMinReviewCount { get; set; } = 3;
        public float TrustAllDimensions { get; set; } = 35f;

        public static ReviewConfig Load(string path)
        {
            if (!File.Exists(path))
                return new ReviewConfig();

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<ReviewConfig>(json) ?? new ReviewConfig();
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
