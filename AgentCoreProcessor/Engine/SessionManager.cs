using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

        /// <summary>闲聊话题创建锁，防止并发创建多个</summary>
        private readonly ConcurrentDictionary<int, SemaphoreSlim> chatTopicLocks = new();

        /// <summary>分类 Core 调用锁，防止并发 Reset 导致消息覆盖</summary>
        private readonly SemaphoreSlim classifyLock = new(1, 1);

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
        /// 轻量用户解析：只做平台用户映射和 Person 查询。
        /// 不进行频道映射、话题分类、消息入库。用于命令系统等不需要完整会话管道的场景。
        /// </summary>
        public async Task<(User User, Person Person)> ResolveUserAsync(IncomingMessage msg)
        {
            var defaultPermission = msg.Platform == "Console"
                ? PermissionLevel.Admin
                : PermissionLevel.Default;
            var user = await users.FindOrCreateAsync(msg.Platform, msg.PlatformUserId, defaultPermission);
            var person = await persons.GetByIdAsync(user.PersonId)
                ?? throw new InvalidOperationException($"User {user.Id} 关联的 Person {user.PersonId} 不存在");
            return (user, person);
        }

        /// <summary>
        /// 处理每条进入的消息：用户映射、频道映射、话题归类、消息入库。
        /// 返回 SessionContext 供 WorkerEngine 使用。
        /// </summary>
        public async Task<SessionContext> OnMessageAsync(IncomingMessage msg)
        {
            // 1. 用户映射 + Person 查询（复用 ResolveUserAsync）
            var (user, person) = await ResolveUserAsync(msg);

            // 2. 更新显示名（适配器每次消息带来最新的群名片/昵称）
            if (!string.IsNullOrEmpty(msg.DisplayName) && msg.DisplayName != user.DisplayName)
            {
                user.DisplayName = msg.DisplayName;
                await users.UpdateAsync(user);
            }

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
            var senderName = !string.IsNullOrEmpty(user.DisplayName) ? user.DisplayName
                           : !string.IsNullOrEmpty(msg.DisplayName) ? msg.DisplayName
                           : msg.PlatformUserId;
            var userMessage = new UserMessage
            {
                UserId = user.Id,
                ChannelId = channel.Id,
                TopicId = topic.Id,
                Content = msg.Content,
                SenderName = senderName,
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

        /// <summary>保存 Lilara 的回复到消息历史。</summary>
        public async Task SaveBotMessageAsync(int topicId, int channelId, string content)
        {
            await messages.SaveAsync(new UserMessage
            {
                UserId = 0,
                ChannelId = channelId,
                TopicId = topicId,
                Content = content,
                SenderName = "Lilara",
                IsFromBot = true,
                Time = DateTime.Now
            });
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

        // ---- 用户管理代理 ----

        public Task<List<User>> GetAllUsersAsync() => users.GetAllAsync();

        public Task<User?> FindUserAsync(string platform, string platformId)
            => users.FindByPlatformAsync(platform, platformId);

        public Task UpdateUserAsync(User user) => users.UpdateAsync(user);

        // ---- Person 管理代理 ----

        public Task<Person?> GetPersonByIdAsync(int id) => persons.GetByIdAsync(id);

        public Task UpdatePersonAsync(Person person) => persons.UpdateAsync(person);

        /// <summary>
        /// 三层话题归类：规则层 → 向量层（双阈值）→ 模型层。闲聊 topic 作为兜底。
        /// </summary>
        private async Task<Topic> ClassifyTopicAsync(User user, Channel channel, IncomingMessage msg)
        {
            // 确保频道有闲聊兜底话题
            var chatTopic = await GetOrCreateChatTopicAsync(channel.Id);

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

                    // 高置信度直接归入（包括闲聊 topic 也参与匹配）
                    if (bestScore > HighConfidenceThreshold && bestTopic != null)
                    {
                        FrameworkLogger.LogTopicClassification("SessionManager", bestTopic.Id,
                            $"vector-high({bestScore:F3})");
                        return bestTopic;
                    }

                    // 低置信度或模糊地带，走模型层
                    var modelResult = await ModelClassifyAsync(msg, channel, chatTopic);
                    FrameworkLogger.LogTopicClassification("SessionManager", modelResult.Id,
                        bestScore < LowConfidenceThreshold
                            ? $"vector-low({bestScore:F3})->model"
                            : $"vector-fuzzy({bestScore:F3})->model");
                    return modelResult;
                }
            }

            // 无活跃话题有 embedding，或 embedding 不可用 → 模型层
            var allActive = await topics.GetActiveByChannelAsync(channel.Id);
            // 只有闲聊话题时，走模型层判断是归闲聊还是新建
            if (allActive.Count <= 1 && allActive.All(t => t.IsChatTopic))
            {
                var modelResult = await ModelClassifyAsync(msg, channel, chatTopic);
                FrameworkLogger.LogTopicClassification("SessionManager", modelResult.Id, "model-only-chat");
                return modelResult;
            }

            var fallback = await ModelClassifyAsync(msg, channel, chatTopic);
            FrameworkLogger.LogTopicClassification("SessionManager", fallback.Id, "model-fallback");
            return fallback;
        }

        /// <summary>
        /// 模型层分类：收集活跃话题摘要+最近消息，调用 TopicClassificationCore。
        /// 闲聊 topic 以 [闲聊] 标记出现在候选列表中。
        /// </summary>
        private async Task<Topic> ModelClassifyAsync(IncomingMessage msg, Channel channel, Topic chatTopic)
        {
            var activeTopics = await topics.GetActiveByChannelAsync(channel.Id);

            var candidates = new List<TopicCandidate>();
            foreach (var t in activeTopics)
            {
                var recentMsgs = await messages.GetRecentByTopicAsync(t.Id, 3);
                var label = t.IsChatTopic ? "[闲聊] " : "";
                candidates.Add(new TopicCandidate
                {
                    TopicId = t.Id,
                    Summary = label + (string.IsNullOrEmpty(t.Summary) ? t.Name : t.Summary),
                    RecentMessages = recentMsgs.Select(m => m.Content).ToList()
                });
            }

            // 如果闲聊 topic 不在活跃列表中（理论上不应该，但防御性处理）
            if (!activeTopics.Any(t => t.IsChatTopic))
            {
                candidates.Add(new TopicCandidate
                {
                    TopicId = chatTopic.Id,
                    Summary = "[闲聊] 日常闲聊和杂谈",
                    RecentMessages = []
                });
            }

            int topicId;
            await classifyLock.WaitAsync();
            try
            {
                topicId = await classificationCore.ClassifyAsync(msg.Content, candidates);
            }
            finally
            {
                classifyLock.Release();
            }

            if (topicId == -2) // chat
                return chatTopic;

            if (topicId == -1) // new
                return await CreateNewTopicAsync(channel.Id, msg);

            var matched = activeTopics.FirstOrDefault(t => t.Id == topicId);
            return matched ?? chatTopic; // 无法匹配时归闲聊而不是新建
        }

        /// <summary>获取或创建频道的闲聊兜底话题。per-channel 加锁防并发重复创建。</summary>
        private async Task<Topic> GetOrCreateChatTopicAsync(int channelId)
        {
            var existing = await topics.GetChatTopicAsync(channelId);
            if (existing != null) return existing;

            var sem = chatTopicLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                // double-check
                existing = await topics.GetChatTopicAsync(channelId);
                if (existing != null) return existing;

                var topic = await topics.CreateAsync(channelId, "闲聊", "日常闲聊和杂谈");
                topic.IsChatTopic = true;
                try
                {
                    var vec = await embeddingProvider.GetEmbeddingAsync("日常闲聊和杂谈");
                    topic.Embedding = VectorUtil.FloatsToBytes(vec);
                }
                catch { }
                await topics.UpdateAsync(topic);
                FrameworkLogger.Log("SessionManager", $"创建闲聊话题: channelId={channelId} topicId={topic.Id}");
                return topic;
            }
            finally
            {
                sem.Release();
            }
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
                // 闲聊话题不更新摘要，保持固定 embedding 防漂移
                if (topic.IsChatTopic) return;
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
