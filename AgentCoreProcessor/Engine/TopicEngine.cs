using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 话题引擎。长生命周期，一个活跃话题一个实例。
    /// 负责消息缓冲聚合和回应决策，决定回复时孵化 WorkerEngine。
    /// </summary>
    internal class TopicEngine : ISubEngine
    {
        public string EngineType => "Topic";
        public bool IsAlive { get; private set; } = true;

        private readonly ISystemContext ctx;
        private readonly int topicId;

        // 消息缓冲
        private readonly object bufferLock = new();
        private readonly List<(IncomingMessage Message, SessionContext Context)> buffer = new();
        private DateTime lastBufferTime;

        // 冲动值
        private float impulse = 0f;
        private DateTime lastImpulseDecay;
        private DateTime? lastResponseTime;

        // 频道亲和度（从 Channel.Affinity 读取，影响 BaseMessageScore 增益）
        private readonly float channelAffinity;

        // 参与人数追踪（影响 BaseMessageScore 折扣）
        private readonly HashSet<int> recentParticipants = new();

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
            recentParticipants.Add(initialContext.User.Id);
            AccumulateImpulse(initialMessage);

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
            recentParticipants.Add(sc.User.Id);
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
                    // 负反馈检查：发言后的下一批消息是否有回应
                    if (awaitingResponse)
                    {
                        bool gotResponse = batch.Any(b => b.Message.IsMentioned);
                        if (gotResponse)
                        {
                            consecutiveIgnores = 0;
                        }
                        else
                        {
                            consecutiveIgnores = Math.Min(consecutiveIgnores + 1, MaxIgnoreBoost);
                        }
                        awaitingResponse = false;
                    }

                    if (ShouldRespond(batch))
                    {
                        SpawnWorker(batch);
                        lastResponseTime = DateTime.Now;
                        impulse = 0f;
                        awaitingResponse = true;
                    }
                }
                // 冷却检查：缓冲空 + 冲动归零 + 超时
                bool bufferEmpty;
                lock (bufferLock) { bufferEmpty = buffer.Count == 0; }
                if (bufferEmpty && impulse <= 0.01f &&
                    (DateTime.Now - lastBufferTime).TotalSeconds > coldTimeoutSeconds)
                {
                    FrameworkLogger.Log("TopicEngine", $"话题冷却退出: topicId={topicId}");
                    IsAlive = false;
                }
            }
        }

        private bool ShouldRespond(List<(IncomingMessage Message, SessionContext Context)> batch)
        {
            // 私聊/控制台：缓冲后一定回复
            if (batch.Any(b => b.Message.IsPrivate))
                return true;

            // 群聊：冷却期内不回复
            if (lastResponseTime != null &&
                (DateTime.Now - lastResponseTime.Value).TotalSeconds < PostResponseCooldownSeconds)
                return false;

            // 群聊：冲动值判断（含负反馈上调）
            float effectiveThreshold = ResponseThreshold + consecutiveIgnores * IgnoreThresholdBoost;
            return impulse >= effectiveThreshold;
        }

        private void AccumulateImpulse(IncomingMessage msg)
        {
            // 参与人数折扣：人少时不必频繁插嘴
            float participantFactor = recentParticipants.Count switch
            {
                <= 1 => 0.6f,
                2 => 0.8f,
                3 => 0.9f,
                _ => 1.0f
            };
            impulse += BaseMessageScore * channelAffinity * participantFactor;

            // 明确意图信号不打折
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

        private void SpawnWorker(List<(IncomingMessage Message, SessionContext Context)> batch)
        {
            // 合并消息内容
            var mergedContent = batch.Count == 1
                ? batch[0].Message.Content
                : string.Join("\n", batch.Select(b => b.Message.Content));

            // 使用最后一条消息的上下文（最新话题状态）
            var lastContext = batch[^1].Context;
            var lastMessage = batch[^1].Message;

            FrameworkLogger.Log("TopicEngine",
                $"孵化 Worker: topicId={topicId}, 消息数={batch.Count}, 合并长度={mergedContent.Length}");

            var worker = new WorkerEngine(ctx, lastMessage, lastContext, mergedContent);
            ctx.StartEngine(worker);
        }

        public void OnEvent(EngineEvent e) { }

        public void RequestStop() => IsAlive = false;
    }
}
