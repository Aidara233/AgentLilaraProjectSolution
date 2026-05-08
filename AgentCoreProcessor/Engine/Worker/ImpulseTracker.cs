using System;
using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 冲动值追踪器。管理消息响应决策的冲动值计算、EMA 衰减和阈值判断。
    /// </summary>
    internal class ImpulseTracker
    {
        private readonly ImpulseConfig config;
        private readonly float channelAffinity;
        private readonly int channelId;

        private float impulse = 0f;
        private DateTime lastImpulseDecay;

        private float expectation = 0f;
        private float reality = 0f;
        private DateTime lastEmaDecay;

        private float messageRate = 0f;
        private DateTime lastMessageRateUpdate;

        public float Impulse => impulse;
        public float MessageRate => messageRate;
        public float Expectation => expectation;
        public float Reality => reality;
        public float ChannelAffinity => channelAffinity;

        public ImpulseTracker(ImpulseConfig config, float channelAffinity, int channelId)
        {
            this.config = config;
            this.channelAffinity = channelAffinity;
            this.channelId = channelId;

            var now = DateTime.Now;
            this.lastImpulseDecay = now;
            this.lastEmaDecay = now;
            this.lastMessageRateUpdate = now;
        }

        public bool ShouldRespond(
            List<(IncomingMessage Message, SessionContext Context)> batch,
            DateTime? lastCompletionTime)
        {
            if (batch.Any(b => b.Message.IsPrivate))
            {
                FrameworkLogger.Log("ImpulseTracker", $"决策: 私聊必回, channelId={channelId}");
                return true;
            }
            if (batch.Any(b => b.Message.IsMentioned))
            {
                FrameworkLogger.Log("ImpulseTracker", $"决策: @提及必回, channelId={channelId}");
                return true;
            }

            if (lastCompletionTime != null &&
                (DateTime.Now - lastCompletionTime.Value).TotalSeconds < config.PostResponseCooldownSeconds)
            {
                FrameworkLogger.Log("ImpulseTracker",
                    $"决策: 发言冷却中, channelId={channelId}, " +
                    $"elapsed={(DateTime.Now - lastCompletionTime.Value).TotalSeconds:F1}s");
                return false;
            }

            float dynamicThreshold = config.BaseThreshold
                + messageRate * config.MessageRateScaleFactor;
            bool respond = impulse >= dynamicThreshold;
            FrameworkLogger.Log("ImpulseTracker",
                $"决策: impulse={impulse:F2}, threshold={dynamicThreshold:F1}" +
                $"(base={config.BaseThreshold}+rate={messageRate:F2}x{config.MessageRateScaleFactor}), " +
                $"ratio={ComputeRatioFactor():F2}(E={expectation:F2}/R={reality:F2}), " +
                $"respond={respond}, channelId={channelId}");
            return respond;
        }

        public void Accumulate(IncomingMessage msg, int participantCount, SessionContext? sc = null)
        {
            float participantFactor = participantCount switch
            {
                <= 1 => 1.0f,
                2 => 0.9f,
                3 => 0.8f,
                _ => 0.6f
            };
            float ratioFactor = ComputeRatioFactor();
            float added = config.BaseMessageScore * channelAffinity * participantFactor * ratioFactor;
            if (msg.IsMentioned) added += config.MentionScore;
            if (msg.IsPrivate) added += config.PrivateScore;
            impulse += added;

            var now = DateTime.Now;
            var elapsed = (float)(now - lastMessageRateUpdate).TotalSeconds;
            if (elapsed > 0)
            {
                float instantRate = 1f / Math.Max(elapsed, 0.1f);
                messageRate = config.MessageRateEmaAlpha * instantRate
                    + (1 - config.MessageRateEmaAlpha) * messageRate;
                lastMessageRateUpdate = now;
            }

            if (sc != null && msg.IsMentioned)
            {
                float trustMult = config.GetTrustMultiplier(sc.Person.TrustLevel);
                reality += config.RealityOnEngagement * trustMult;
            }

            FrameworkLogger.Log("ImpulseTracker",
                $"冲动值+{added:F2}: impulse={impulse:F2}, ratio={ratioFactor:F2}, " +
                $"affinity={channelAffinity:F2}, participants={participantCount}, " +
                $"msgRate={messageRate:F2}, mentioned={msg.IsMentioned}, channelId={channelId}");
        }

        public void Decay()
        {
            var now = DateTime.Now;
            var elapsed = (float)(now - lastImpulseDecay).TotalSeconds;
            lastImpulseDecay = now;
            impulse = Math.Max(0f, impulse - config.DecayPerSecond * elapsed);

            var emaElapsed = (float)(now - lastEmaDecay).TotalSeconds;
            lastEmaDecay = now;
            float decayFactor = MathF.Pow(config.EmaDecayRate, emaElapsed);
            expectation *= decayFactor;
            reality *= decayFactor;
        }

        public void ApplyPostResponseUpdate(bool wasMentionTriggered)
        {
            float dynamicThreshold = config.BaseThreshold + messageRate * config.MessageRateScaleFactor;
            // 回复后将冲动值压到阈值以下，防止堆积导致连续触发
            impulse = Math.Max(0f, Math.Min(impulse - dynamicThreshold, dynamicThreshold * 0.5f));
            if (wasMentionTriggered)
                expectation += config.ExpectationOnMentionTriggered;
            else
                expectation += config.ExpectationOnProactive;
        }

        private float ComputeRatioFactor()
        {
            float effectiveExpectation = Math.Max(expectation, config.BaseExpectation);
            float ratio = reality / effectiveExpectation;
            return Math.Clamp(ratio, config.RatioFactorLower, config.RatioFactorUpper);
        }
    }
}
