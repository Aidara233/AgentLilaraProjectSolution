using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Util;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 会话管理器，负责消息的 Topic 归类、用户/频道映射、消息入库。
    /// 所有消息（无论是否触发 Lilara）都经过这里。
    /// </summary>
    internal class SessionManager
    {
        private readonly UserRepository users;
        private readonly PersonRepository persons;
        private readonly ChannelRepository channels;
        private readonly TopicRepository topics;
        private readonly MessageRepository messages;
        private readonly IEmbeddingProvider embeddingProvider;

        private readonly TopicClassificationCore classificationCore = new();
        private readonly TopicSummaryCore summaryCore = new();

        /// <summary>话题超时时间，超过此时间未活跃的话题自动关闭</summary>
        private readonly TimeSpan topicTimeout = TimeSpan.FromHours(2);

        /// <summary>获取上下文时默认返回的最近消息数量</summary>
        private const int DefaultContextLimit = 20;

        // 向量层参数
        private const float SimilarityWeight = 0.7f;
        private const float RecencyWeight = 0.3f;
        private const float HighConfidenceThreshold = 0.8f;
        private const float LowConfidenceThreshold = 0.5f;
        private const double DecayHalfLifeMinutes = 30.0;

        /// <summary>每隔多少条消息触发摘要更新</summary>
        private const int SummaryUpdateInterval = 5;

        public SessionManager(
            UserRepository users,
            PersonRepository persons,
            ChannelRepository channels,
            TopicRepository topics,
            MessageRepository messages,
            IEmbeddingProvider embeddingProvider)
        {
            this.users = users;
            this.persons = persons;
            this.channels = channels;
            this.topics = topics;
            this.messages = messages;
            this.embeddingProvider = embeddingProvider;
        }

        /// <summary>
        /// 处理每条进入的消息：用户映射、频道映射、话题归类、消息入库。
        /// 返回 SessionContext 供 WorkerEngine 使用。
        /// </summary>
        public async Task<SessionContext> OnMessageAsync(IncomingMessage msg)
        {
            // 1. 用户映射：平台用户 → 内部 User（自动创建 Person）
            // 控制台用户默认 Admin 权限
            var defaultPermission = msg.Platform == "Console"
                ? PermissionLevel.Admin
                : PermissionLevel.Default;
            var user = await users.FindOrCreateAsync(msg.Platform, msg.PlatformUserId, defaultPermission);

            // 2. 获取关联的自然人
            var person = await persons.GetByIdAsync(user.PersonId)
                ?? throw new InvalidOperationException($"User {user.Id} 关联的 Person {user.PersonId} 不存在");

            // 3. 频道映射
            var channel = await channels.FindOrCreateAsync(msg.ChannelId);

            // 4. 清理过期话题
            await topics.DeactivateStaleAsync(topicTimeout);

            // 5. 话题归类（三层：规则→向量→模型）
            var topic = await ClassifyTopicAsync(user, channel, msg);

            // 6. 更新话题活跃时间和消息计数
            topic.LastMessageTime = msg.Time;
            topic.MessageCount++;
            await topics.UpdateAsync(topic);

            // 7. 消息入库
            var userMessage = new UserMessage
            {
                UserId = user.Id,
                ChannelId = channel.Id,
                TopicId = topic.Id,
                Content = msg.Content,
                Time = msg.Time
            };
            await messages.SaveAsync(userMessage);

            // 8. 摘要后处理（异步，不阻塞主流程）
            _ = PostProcessSummaryAsync(topic);

            // 9. 获取最近历史消息
            var recent = await GetContextAsync(topic.Id);

            return new SessionContext
            {
                User = user,
                Person = person,
                Channel = channel,
                Topic = topic,
                RecentMessages = recent
            };
        }

        /// <summary>获取指定话题的历史消息（最近 N 条，按时间升序）。</summary>
        public async Task<List<UserMessage>> GetContextAsync(int topicId, int limit = DefaultContextLimit)
        {
            var msgs = await messages.GetRecentByTopicAsync(topicId, limit);
            // GetRecentByTopicAsync 返回的是 DESC 排序，反转为升序
            msgs.Reverse();
            return msgs;
        }

        /// <summary>获取频道内所有活跃话题。</summary>
        public Task<List<Topic>> GetActiveTopicsAsync(int channelId)
        {
            return topics.GetActiveByChannelAsync(channelId);
        }

        public Task<List<Channel>> GetAllChannelsAsync()
        {
            return channels.GetAllAsync();
        }

        public Task<Channel?> GetChannelByIdAsync(int id) => channels.GetByIdAsync(id);

        public Task UpdateChannelAsync(Channel channel) => channels.UpdateAsync(channel);

        /// <summary>
        /// 三层话题归类：规则层 → 向量层（双阈值）→ 模型层。
        /// </summary>
        private async Task<Topic> ClassifyTopicAsync(User user, Channel channel, IncomingMessage msg)
        {
            // 规则层：回复/引用关系 → 归入被回复消息所属的 Topic
            if (!string.IsNullOrEmpty(msg.ReplyTo) && int.TryParse(msg.ReplyTo, out var replyMsgId))
            {
                var replyMsg = await messages.GetByIdAsync(replyMsgId);
                if (replyMsg != null)
                {
                    var replyTopic = await topics.GetByIdAsync(replyMsg.TopicId);
                    if (replyTopic != null && replyTopic.IsActive)
                    {
                        FrameworkLogger.LogTopicClassification("SessionManager", replyTopic.Id, "rule-reply");
                        return replyTopic;
                    }
                }
            }

            // 向量层：对活跃话题的 Summary embedding 计算相似度 + 时间衰减
            var activeWithEmbedding = await topics.GetActiveWithEmbeddingAsync(channel.Id);
            if (activeWithEmbedding.Count > 0)
            {
                float[]? msgVec = null;
                try
                {
                    msgVec = await embeddingProvider.GetEmbeddingAsync(msg.Content);
                }
                catch (Exception)
                {
                    // embedding 不可用时跳过向量层，直接走模型层
                }

                if (msgVec != null)
                {
                    var bestTopic = (Topic?)null;
                    var bestScore = 0f;

                    foreach (var t in activeWithEmbedding)
                    {
                        float similarity = VectorUtil.ComputeSimilarity(msgVec, t.Embedding);
                        double minutesSinceLastMsg = (msg.Time - t.LastMessageTime).TotalMinutes;
                        float timeDecay = (float)Math.Exp(-minutesSinceLastMsg / DecayHalfLifeMinutes);
                        float score = similarity * SimilarityWeight + timeDecay * RecencyWeight;

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestTopic = t;
                        }
                    }

                    // 双阈值判定
                    if (bestScore > HighConfidenceThreshold && bestTopic != null)
                    {
                        FrameworkLogger.LogTopicClassification("SessionManager", bestTopic.Id,
                            $"vector-high({bestScore:F3})");
                        return bestTopic;
                    }

                    if (bestScore < LowConfidenceThreshold)
                    {
                        // 低置信度，走模型层但倾向新建
                        var modelResult = await ModelClassifyAsync(msg, channel);
                        FrameworkLogger.LogTopicClassification("SessionManager", modelResult.Id,
                            $"vector-low({bestScore:F3})->model");
                        return modelResult;
                    }

                    // 模糊地带，走模型层确认
                    var confirmed = await ModelClassifyAsync(msg, channel);
                    FrameworkLogger.LogTopicClassification("SessionManager", confirmed.Id,
                        $"vector-fuzzy({bestScore:F3})->model");
                    return confirmed;
                }
            }

            // 无活跃话题有 embedding，或 embedding 不可用 → 模型层
            // 如果没有任何活跃话题，直接新建
            var allActive = await topics.GetActiveByChannelAsync(channel.Id);
            if (allActive.Count == 0)
            {
                var newTopic = await CreateNewTopicAsync(channel.Id, msg);
                FrameworkLogger.LogTopicClassification("SessionManager", newTopic.Id, "new-no-active");
                return newTopic;
            }

            var fallback = await ModelClassifyAsync(msg, channel);
            FrameworkLogger.LogTopicClassification("SessionManager", fallback.Id, "model-fallback");
            return fallback;
        }

        /// <summary>
        /// 模型层分类：收集活跃话题摘要+最近消息，调用 TopicClassificationCore。
        /// </summary>
        private async Task<Topic> ModelClassifyAsync(IncomingMessage msg, Channel channel)
        {
            var activeTopics = await topics.GetActiveByChannelAsync(channel.Id);
            if (activeTopics.Count == 0)
                return await CreateNewTopicAsync(channel.Id, msg);

            var candidates = new List<TopicCandidate>();
            foreach (var t in activeTopics)
            {
                var recentMsgs = await messages.GetRecentByTopicAsync(t.Id, 3);
                candidates.Add(new TopicCandidate
                {
                    TopicId = t.Id,
                    Summary = string.IsNullOrEmpty(t.Summary) ? t.Name : t.Summary,
                    RecentMessages = recentMsgs.Select(m => m.Content).ToList()
                });
            }

            var topicId = await classificationCore.ClassifyAsync(msg.Content, candidates);

            if (topicId == -1)
                return await CreateNewTopicAsync(channel.Id, msg);

            var matched = activeTopics.FirstOrDefault(t => t.Id == topicId);
            return matched ?? await CreateNewTopicAsync(channel.Id, msg);
        }

        /// <summary>
        /// 创建新话题，生成初始摘要和 embedding。
        /// </summary>
        private async Task<Topic> CreateNewTopicAsync(int channelId, IncomingMessage msg)
        {
            var topicName = msg.Content.Length > 20
                ? msg.Content[..20] + "..."
                : msg.Content;
            var topic = await topics.CreateAsync(channelId, topicName);

            // 生成初始摘要
            try
            {
                var summary = await summaryCore.GenerateSummaryAsync(topicName, [msg.Content]);
                topic.Summary = summary;

                var vec = await embeddingProvider.GetEmbeddingAsync(summary);
                topic.Embedding = VectorUtil.FloatsToBytes(vec);

                await topics.UpdateAsync(topic);
            }
            catch (Exception)
            {
                // 摘要/embedding 生成失败不阻塞话题创建
            }

            return topic;
        }

        /// <summary>
        /// 摘要后处理：每 N 条消息更新一次摘要和 embedding。
        /// </summary>
        private async Task PostProcessSummaryAsync(Topic topic)
        {
            try
            {
                if (topic.MessageCount % SummaryUpdateInterval != 0) return;

                var recentMsgs = await messages.GetRecentByTopicAsync(topic.Id, SummaryUpdateInterval);
                var msgContents = recentMsgs.Select(m => m.Content).ToList();

                string newSummary;
                if (string.IsNullOrEmpty(topic.Summary))
                    newSummary = await summaryCore.GenerateSummaryAsync(topic.Name, msgContents);
                else
                    newSummary = await summaryCore.UpdateSummaryAsync(topic.Summary, msgContents);

                topic.Summary = newSummary;

                var vec = await embeddingProvider.GetEmbeddingAsync(newSummary);
                topic.Embedding = VectorUtil.FloatsToBytes(vec);

                await topics.UpdateAsync(topic);
                FrameworkLogger.Log("SessionManager", $"话题摘要更新: topic={topic.Id}");
            }
            catch (Exception)
            {
                // 摘要更新失败不影响主流程
            }
        }
    }
}
