using System;
using System.Collections.Generic;
using System.Linq;
using AgentCoreProcessor.Adapter;

namespace AgentCoreProcessor.Engine
{
    internal class ImpulseTracker
    {
        private readonly ImpulseConfig config;
        private readonly float channelAffinity;
        private readonly int channelId;

        private float impulse = 0f;
        private DateTime lastDecayTime;

        public float Impulse => impulse;
        public float ChannelAffinity => channelAffinity;

        public ImpulseTracker(ImpulseConfig config, float channelAffinity, int channelId)
        {
            this.config = config;
            this.channelAffinity = channelAffinity;
            this.channelId = channelId;
            this.lastDecayTime = DateTime.Now;
        }

        public bool ShouldRespond(
            List<(IncomingMessage Message, SessionContext Context)> batch,
            DateTime? lastCompletionTime)
        {
            if (batch.Any(b => b.Message.IsPrivate))
            {
                return true;
            }
            if (batch.Any(b => b.Message.IsMentioned && !b.Message.IsSystemEvent))
            {
                return true;
            }

            if (lastCompletionTime != null &&
                (DateTime.Now - lastCompletionTime.Value).TotalSeconds < config.PostResponseCooldownSeconds)
            {
                return false;
            }

            bool respond = impulse >= config.Threshold;
            return respond;
        }

        public void Accumulate(IncomingMessage msg, int participantCount, bool isSystemEvent = false)
        {
            ApplyDecay();

            float added;
            if (isSystemEvent)
            {
                added = msg.IsMentioned ? config.MentionBonus : 0f;
            }
            else
            {
                added = config.BaseScore
                    + config.AffinityBonusMax * channelAffinity
                    - config.GetParticipantDiscount(participantCount);
                if (msg.IsMentioned) added += config.MentionBonus;
                added = Math.Max(0f, added);
            }
            impulse += added;

        }

        public void ApplyPostResponseUpdate()
        {
            impulse = Math.Min(impulse, config.PostResponseCap);
        }

        private void ApplyDecay()
        {
            var now = DateTime.Now;
            var elapsed = (float)(now - lastDecayTime).TotalSeconds;
            lastDecayTime = now;
            if (elapsed <= 0) return;

            float lambda = 0.693f / config.DecayHalfLifeSeconds;
            impulse *= MathF.Exp(-lambda * elapsed);
        }
    }
}
