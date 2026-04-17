using System.IO;
using AgentCoreProcessor.Database;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    internal class ImpulseConfig
    {
        public float BaseMessageScore { get; set; } = 1f;
        public float MentionScore { get; set; } = 8f;
        public float PrivateScore { get; set; } = 8f;

        public float DecayPerSecond { get; set; } = 0.1f;

        public float BaseThreshold { get; set; } = 3f;
        public float MessageRateScaleFactor { get; set; } = 0.5f;

        public float EmaDecayRate { get; set; } = 0.995f;
        public float BaseExpectation { get; set; } = 0.5f;
        public float ExpectationOnProactive { get; set; } = 2.0f;
        public float ExpectationOnMentionTriggered { get; set; } = 0.5f;
        public float RealityOnEngagement { get; set; } = 2.0f;
        public float RatioFactorLower { get; set; } = 0.3f;
        public float RatioFactorUpper { get; set; } = 2.0f;

        public float PostResponseCooldownSeconds { get; set; } = 3f;
        public float BufferWindowSeconds { get; set; } = 2.5f;
        public float ColdTimeoutSeconds { get; set; } = 600f;

        public float MessageRateEmaAlpha { get; set; } = 0.1f;

        public float TrustMultiplierHostile { get; set; } = 0.1f;
        public float TrustMultiplierWary { get; set; } = 0.3f;
        public float TrustMultiplierUnknown { get; set; } = 0.5f;
        public float TrustMultiplierStranger { get; set; } = 0.7f;
        public float TrustMultiplierUnderstanding { get; set; } = 1.0f;
        public float TrustMultiplierFamiliarity { get; set; } = 1.3f;
        public float TrustMultiplierTrust { get; set; } = 1.6f;
        public float TrustMultiplierAbsoluteTrust { get; set; } = 2.0f;

        public float GetTrustMultiplier(TrustLevel level) => level switch
        {
            TrustLevel.Hostile => TrustMultiplierHostile,
            TrustLevel.Wary => TrustMultiplierWary,
            TrustLevel.Unknown => TrustMultiplierUnknown,
            TrustLevel.Stranger => TrustMultiplierStranger,
            TrustLevel.Understanding => TrustMultiplierUnderstanding,
            TrustLevel.Familiarity => TrustMultiplierFamiliarity,
            TrustLevel.Trust => TrustMultiplierTrust,
            TrustLevel.AbsoluteTrust => TrustMultiplierAbsoluteTrust,
            _ => 1.0f
        };

        public static ImpulseConfig Load(string path)
        {
            if (!File.Exists(path)) return new ImpulseConfig();
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<ImpulseConfig>(json) ?? new ImpulseConfig();
        }
    }
}
