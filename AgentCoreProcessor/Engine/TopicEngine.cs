using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 话题引擎（社交网关）。长生命周期，一个活跃话题一个实例。
    /// 负责消息缓冲聚合、冲动值决策、参与者追踪。
    /// 决定回复时通过 Activate 激活常驻的 WorkerEngine。
    /// </summary>
    internal class TopicEngine : ISubEngine
    {
        public string EngineType => "Topic";
        public bool IsAlive { get; private set; } = true;

        private readonly ISystemContext ctx;
        private readonly int topicId;
        private readonly WorkerEngine worker;

        // 消息缓冲
        private readonly object bufferLock = new();
        private readonly List<(IncomingMessage Message, SessionContext Context)> buffer = new();
        private DateTime lastBufferTime;

        // 冲动值
        private float impulse = 0f;
        private DateTime lastImpulseDecay;

        // 频道亲和度
        private readonly float channelAffinity;

        // 参与者追踪
        private readonly ConcurrentDictionary<int, ParticipantInfo> recentParticipants = new();

        // 负反馈抑制
        private bool awaitingResponse = false;
        private int consecutiveIgnores = 0;

        // 配置常量
        private const float BufferWindowSeconds = 2.5f;
        private readonly float coldTimeoutSeconds;
        private const float MentionScore = 8f;
        private const float BaseMessageScore = 1f;
        private const float PrivateScore = 8f;
        private const float DecayPerSecond = 0.5f;
        private const float ResponseThreshold = 3f;
        private const float PostResponseCooldownSeconds = 3f;
        private const float IgnoreThresholdBoost = 1.5f;
        private const int MaxIgnoreBoost = 3;

        public TopicEngine(ISystemContext ctx, SessionContext initialContext, IncomingMessage initialMessage)
        {
            this.ctx = ctx;
            this.topicId = initialContext.Topic.Id;
            this.channelAffinity = initialContext.Channel.Affinity;
            this.coldTimeoutSeconds = initialContext.Topic.IsChatTopic ? 600f : 300f;
            this.lastImpulseDecay = DateTime.Now;
            this.lastBufferTime = DateTime.Now;

            buffer.Add((initialMessage, initialContext));
            recentParticipants.TryAdd(initialContext.User.Id, ParticipantInfo.From(initialContext.User, initialMessage));
            AccumulateImpulse(initialMessage);

            // 创建常驻 WorkerEngine
            worker = new WorkerEngine(ctx, topicId);
            ctx.StartEngine(worker);

            FrameworkLogger.Log("TopicEngine", $"创建: topicId={topicId}, affinity={channelAffinity:F2}");
        }

        /// <summary>由 TopicEngineSpawnCheck 直接调用，将新消息加入缓冲。</summary>
        public void EnqueueMessage(IncomingMessage msg, SessionContext sc)
        {
            lock (bufferLock)
            {
                buffer.Add((msg, sc));
                lastBufferTime = DateTime.Now;
            }
            recentParticipants.AddOrUpdate(
                sc.User.Id,
                ParticipantInfo.From(sc.User, msg),
                (_, _) => ParticipantInfo.From(sc.User, msg));
            AccumulateImpulse(msg);
        }

        public async Task RunAsync()
        {
            while (IsAlive)
            {
                await Task.Delay(200);

                DecayImpulse();

                // 检查缓冲窗口是否到期
                List<(IncomingMessage Message, SessionContext Context)>? batch = null;
                lock (bufferLock)
                {
                    if (buffer.Count > 0 &&
                        (DateTime.Now - lastBufferTime).TotalSeconds >= BufferWindowSeconds)
                    {
                        batch = new(buffer);
                        buffer.Clear();
                    }
                }

                if (batch != null)
                {
                    // 负反馈检查
                    if (awaitingResponse)
                    {
                        bool gotResponse = batch.Any(b => b.Message.IsMentioned);
                        if (gotResponse)
                            consecutiveIgnores = 0;
                        else
                            consecutiveIgnores = Math.Min(consecutiveIgnores + 1, MaxIgnoreBoost);
                        awaitingResponse = false;
                    }

                    if (!ctx.MuteMode && ShouldRespond(batch))
                    {
                        var snapshot = new Dictionary<int, ParticipantInfo>(recentParticipants);
                        worker.Activate(new ActivationBatch
                        {
                            Messages = batch,
                            ParticipantSnapshot = snapshot
                        });
                        impulse = 0f;
                        awaitingResponse = true;
                    }
                }

                // 冷却检查
                bool bufferEmpty;
                lock (bufferLock) { bufferEmpty = buffer.Count == 0; }
                if (bufferEmpty && impulse <= 0.01f &&
                    (DateTime.Now - lastBufferTime).TotalSeconds > coldTimeoutSeconds)
                {
                    worker.RequestStop();
                    FrameworkLogger.Log("TopicEngine", $"话题冷却退出: topicId={topicId}");
                    IsAlive = false;
                }
            }
        }

        private bool ShouldRespond(List<(IncomingMessage Message, SessionContext Context)> batch)
        {
            // 私聊：必回
            if (batch.Any(b => b.Message.IsPrivate))
                return true;

            // @提及：穿透冷却，必回
            if (batch.Any(b => b.Message.IsMentioned))
                return true;

            // 冷却期：从 Worker 完成时算起
            if (worker.LastCompletionTime != null &&
                (DateTime.Now - worker.LastCompletionTime.Value).TotalSeconds < PostResponseCooldownSeconds)
                return false;

            // 冲动值判断
            float effectiveThreshold = ResponseThreshold + consecutiveIgnores * IgnoreThresholdBoost;
            return impulse >= effectiveThreshold;
        }

        private void AccumulateImpulse(IncomingMessage msg)
        {
            float participantFactor = recentParticipants.Count switch
            {
                <= 1 => 0.6f,
                2 => 0.8f,
                3 => 0.9f,
                _ => 1.0f
            };
            impulse += BaseMessageScore * channelAffinity * participantFactor;

            if (msg.IsMentioned) impulse += MentionScore;
            if (msg.IsPrivate) impulse += PrivateScore;
        }

        private void DecayImpulse()
        {
            var now = DateTime.Now;
            var elapsed = (float)(now - lastImpulseDecay).TotalSeconds;
            lastImpulseDecay = now;
            impulse = Math.Max(0f, impulse - DecayPerSecond * elapsed);
        }

        public void OnEvent(EngineEvent e) { }

        public void RequestStop()
        {
            worker.RequestStop();
            IsAlive = false;
        }
    }
}
