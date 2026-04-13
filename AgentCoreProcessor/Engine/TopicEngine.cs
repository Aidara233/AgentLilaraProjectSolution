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

        // 配置常量
        private const float BufferWindowSeconds = 2.5f;
        private const float ColdTimeoutSeconds = 300f;
        private const float MentionScore = 8f;
        private const float BaseMessageScore = 1f;
        private const float PrivateScore = 8f;
        private const float DecayPerSecond = 0.5f;
        private const float ResponseThreshold = 3f;
        private const float PostResponseCooldownSeconds = 3f;
        public TopicEngine(ISystemContext ctx, SessionContext initialContext, IncomingMessage initialMessage)
        {
            this.ctx = ctx;
            this.topicId = initialContext.Topic.Id;
            this.lastImpulseDecay = DateTime.Now;
            this.lastBufferTime = DateTime.Now;

            buffer.Add((initialMessage, initialContext));
            AccumulateImpulse(initialMessage);

            FrameworkLogger.Log("TopicEngine", $"创建: topicId={topicId}");
        }

        /// <summary>由 TopicEngineSpawnCheck 直接调用，将新消息加入缓冲。</summary>
        public void EnqueueMessage(IncomingMessage msg, SessionContext sc)
        {
            lock (bufferLock)
            {
                buffer.Add((msg, sc));
                lastBufferTime = DateTime.Now;
            }
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

                if (batch != null && ShouldRespond(batch))
                {
                    SpawnWorker(batch);
                    lastResponseTime = DateTime.Now;
                    impulse = 0f;
                }
                // 冷却检查：缓冲空 + 冲动归零 + 超时
                bool bufferEmpty;
                lock (bufferLock) { bufferEmpty = buffer.Count == 0; }
                if (bufferEmpty && impulse <= 0.01f &&
                    (DateTime.Now - lastBufferTime).TotalSeconds > ColdTimeoutSeconds)
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

            // 群聊：冲动值判断
            return impulse >= ResponseThreshold;
        }

        private void AccumulateImpulse(IncomingMessage msg)
        {
            impulse += BaseMessageScore;
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
