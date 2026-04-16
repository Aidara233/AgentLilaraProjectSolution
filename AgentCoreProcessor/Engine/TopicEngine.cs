using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Memory;

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
        private readonly ConcurrentDictionary<int, byte> recentParticipants = new();

        // 负反馈抑制
        private bool awaitingResponse = false;
        private int consecutiveIgnores = 0;

        // 记忆缓存：per-person，避免同话题反复查询
        private readonly Dictionary<int, (List<ScoredMemory> Results, DateTime Time)> memoryCache = new();
        private const float MemoryCacheTtlSeconds = 60f;

        // 记忆提取：每 N 条消息触发一次
        private int processedMessageCount = 0;
        private const int MemoryExtractionInterval = 3;
        private SessionContext? lastContext;

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
            recentParticipants.TryAdd(initialContext.User.Id, 0);
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
            recentParticipants.TryAdd(sc.User.Id, 0);
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

                    // 记忆提取计数（无论是否回复都要跑）
                    TrackMemoryExtraction(batch);

                    if (!ctx.MuteMode && ShouldRespond(batch))
                    {
                        await SpawnWorkerAsync(batch);
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
                    // 冷却退出前，剩余消息强制提取记忆
                    if (processedMessageCount > 0 && lastContext != null)
                        await ExtractMemoryAsync(lastContext);

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

        private async Task SpawnWorkerAsync(List<(IncomingMessage Message, SessionContext Context)> batch)
        {
            // 合并消息内容
            var mergedContent = batch.Count == 1
                ? batch[0].Message.Content
                : string.Join("\n", batch.Select(b => b.Message.Content));

            // 收集图片本地路径
            var imagePaths = batch
                .Where(b => b.Message.Attachments != null)
                .SelectMany(b => b.Message.Attachments!)
                .Where(a => a.Type == AttachmentType.Image && !string.IsNullOrEmpty(a.LocalPath))
                .Select(a => a.LocalPath!)
                .ToList();

            if (imagePaths.Count > 0)
            {
                // 给图片消息加语义帧
                var prefix = imagePaths.Count == 1 ? "（用户发送了一张图片）" : $"（用户发送了{imagePaths.Count}张图片）";
                mergedContent = string.IsNullOrEmpty(mergedContent)
                    ? prefix
                    : $"{mergedContent}\n\n{prefix}";
            }

            // 使用最后一条消息的上下文（最新话题状态）
            var lastContext = batch[^1].Context;
            var lastMessage = batch[^1].Message;

            // 查缓存或新查询记忆
            var memory = await GetCachedMemoryAsync(lastContext, mergedContent);

            FrameworkLogger.Log("TopicEngine",
                $"孵化 Worker: topicId={topicId}, 消息数={batch.Count}, 合并长度={mergedContent.Length}" +
                (imagePaths.Count > 0 ? $", 图片={imagePaths.Count}" : ""));

            var worker = new WorkerEngine(ctx, lastMessage, lastContext, mergedContent,
                preloadedMemory: memory,
                imagePaths: imagePaths.Count > 0 ? imagePaths : null);
            ctx.StartEngine(worker);
        }

        /// <summary>记忆提取计数，达到间隔时异步触发提取。独立于是否回复。</summary>
        private void TrackMemoryExtraction(List<(IncomingMessage Message, SessionContext Context)> batch)
        {
            var batchContext = batch[^1].Context;
            this.lastContext = batchContext;
            processedMessageCount += batch.Count;
            if (processedMessageCount >= MemoryExtractionInterval)
            {
                processedMessageCount = 0;
                _ = ExtractMemoryAsync(batchContext);
            }
        }

        /// <summary>
        /// 获取记忆上下文，优先返回缓存。按 personId 缓存，TTL 内复用。
        /// </summary>
        private async Task<List<ScoredMemory>> GetCachedMemoryAsync(SessionContext context, string query)
        {
            int personId = context.Person.Id;

            if (memoryCache.TryGetValue(personId, out var cached) &&
                (DateTime.Now - cached.Time).TotalSeconds < MemoryCacheTtlSeconds)
            {
                FrameworkLogger.Log("TopicEngine", $"记忆缓存命中: personId={personId}");
                return cached.Results;
            }

            try
            {
                var results = await ctx.MemorySvc.RecallAsync(
                    personId, context.Channel.Id, topicId,
                    query, topK: 10, includeLinks: true, includePersona: true);
                memoryCache[personId] = (results, DateTime.Now);
                return results;
            }
            catch
            {
                return new List<ScoredMemory>();
            }
        }

        /// <summary>异步提取对话中的记忆事实和反馈，写入临时记忆库。</summary>
        private async Task ExtractMemoryAsync(SessionContext context)
        {
            try
            {
                var recent = await ctx.Session.GetContextAsync(topicId, limit: 10);
                if (recent.Count < 2) return;

                var lines = recent.Select(m =>
                    $"{(m.IsFromBot ? "Lilara" : "用户")}: {m.Content}").ToList();

                var core = new MemoryExtractionCore();
                var results = await core.ExtractAsync(lines);

                int factCount = 0;
                int feedbackCount = 0;

                foreach (var item in results)
                {
                    if (item.Type == "feedback" && item.Sentiment != null)
                    {
                        await ctx.MemorySvc.ApplyFeedbackAsync(
                            context.Person.Id, item.Content,
                            item.Sentiment, item.Correction);
                        feedbackCount++;
                    }
                    else
                    {
                        await ctx.MemorySvc.StoreAsync(item.Content,
                            context.Person.Id, context.Channel.Id, topicId,
                            confidence: item.Confidence);
                        factCount++;
                    }
                }

                if (factCount + feedbackCount > 0)
                    FrameworkLogger.Log("TopicEngine",
                        $"记忆提取: topicId={topicId}, 事实{factCount}条, 反馈{feedbackCount}条");
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("TopicEngine", $"记忆提取失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 为批次中所有图片附件生成文字描述。无视觉模型或无图片时返回 null。
        /// </summary>
        private async Task<string?> GenerateImageDescriptionsAsync(
            List<(IncomingMessage Message, SessionContext Context)> batch, string textContext)
        {
            if (ctx.Vision == null) return null;

            var imageAttachments = batch
                .Where(b => b.Message.Attachments != null)
                .SelectMany(b => b.Message.Attachments!)
                .Where(a => a.Type == AttachmentType.Image && !string.IsNullOrEmpty(a.LocalPath))
                .ToList();

            if (imageAttachments.Count == 0) return null;

            var descriptions = new List<string>();
            var contextHint = textContext.Length > 200 ? textContext[..200] : textContext;

            foreach (var att in imageAttachments)
            {
                try
                {
                    var desc = await ctx.Vision.DescribeImageAsync(att.LocalPath!, contextHint);
                    att.Description = desc;
                    descriptions.Add($"[图片描述] {desc}");
                    FrameworkLogger.Log("TopicEngine", $"图片描述完成: {att.FileName}");
                }
                catch (Exception ex)
                {
                    descriptions.Add("[图片：描述生成失败]");
                    FrameworkLogger.Log("TopicEngine", $"图片描述失败: {ex.Message}");
                }
            }

            return descriptions.Count > 0 ? string.Join("\n", descriptions) : null;
        }

        public void OnEvent(EngineEvent e) { }

        public void RequestStop() => IsAlive = false;
    }
}
