using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 会话管理器，负责用户/频道映射、消息入库。
    /// 实时阶段不做话题分类，消息落入频道的未分类话题；做梦阶段再归档。
    /// </summary>
    internal class SessionManager
    {
        private readonly UserRepository users;
        private readonly PersonRepository persons;
        private readonly ChannelRepository channels;
        private readonly TopicRepository topics;
        private readonly MessageRepository messages;

        /// <summary>获取上下文时默认返回的最近消息数量</summary>
        private const int DefaultContextLimit = 20;

        /// <summary>未分类话题创建锁，防止并发创建多个</summary>
        private readonly ConcurrentDictionary<int, SemaphoreSlim> unclassifiedTopicLocks = new();

        public SessionManager(
            UserRepository users,
            PersonRepository persons,
            ChannelRepository channels,
            TopicRepository topics,
            MessageRepository messages)
        {
            this.users = users;
            this.persons = persons;
            this.channels = channels;
            this.topics = topics;
            this.messages = messages;
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
        /// 处理每条进入的消息：用户映射、频道映射、消息入库。
        /// 实时阶段不做话题分类，消息落入频道的未分类话题。
        /// </summary>
        public async Task<SessionContext> OnMessageAsync(IncomingMessage msg)
        {
            // 1. 用户映射 + Person 查询
            var (user, person) = await ResolveUserAsync(msg);

            // 2. 更新显示名
            if (!string.IsNullOrEmpty(msg.DisplayName) && msg.DisplayName != user.DisplayName)
            {
                user.DisplayName = msg.DisplayName;
                await users.UpdateAsync(user);
            }

            // 3. 频道映射
            var channel = await channels.FindOrCreateAsync(msg.ChannelId);

            // 4. 清理过期话题
            await topics.DeactivateStaleAsync(TimeSpan.FromHours(2));

            // 5. 未分类话题（不做分类）
            var topic = await GetOrCreateUnclassifiedTopicAsync(channel.Id);

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

            // 8. 获取最近历史消息（按频道）
            var recent = await GetContextByChannelAsync(channel.Id);

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

        // ---- 未分类话题管理 ----

        /// <summary>获取或创建频道的未分类话题。per-channel 加锁防并发重复创建。</summary>
        private async Task<Topic> GetOrCreateUnclassifiedTopicAsync(int channelId)
        {
            var existing = await topics.GetUnclassifiedTopicAsync(channelId);
            if (existing != null) return existing;

            var sem = unclassifiedTopicLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                existing = await topics.GetUnclassifiedTopicAsync(channelId);
                if (existing != null) return existing;

                var topic = await topics.CreateAsync(channelId, "未分类");
                topic.IsUnclassified = true;
                await topics.UpdateAsync(topic);
                FrameworkLogger.Log("SessionManager", $"创建未分类话题: channelId={channelId} topicId={topic.Id}");
                return topic;
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>获取指定频道的未分类话题（公开，做梦用）。</summary>
        public Task<Topic?> GetUnclassifiedTopicAsync(int channelId)
            => topics.GetUnclassifiedTopicAsync(channelId);

        /// <summary>获取指定频道中未分类的消息（做梦分段用）。</summary>
        public async Task<List<UserMessage>> GetUnclassifiedMessagesAsync(int channelId, DateTime since)
        {
            var unclassified = await topics.GetUnclassifiedTopicAsync(channelId);
            if (unclassified == null) return new List<UserMessage>();
            return await messages.GetUnclassifiedByChannelAsync(channelId, unclassified.Id, since);
        }

        /// <summary>批量重新分配消息的话题（做梦归档用）。</summary>
        public Task<int> ReassignMessagesAsync(List<int> messageIds, int newTopicId)
            => messages.UpdateTopicIdBatchAsync(messageIds, newTopicId);

        /// <summary>获取指定频道的最近历史消息（按时间升序）。</summary>
        public async Task<List<UserMessage>> GetContextByChannelAsync(int channelId, int limit = DefaultContextLimit)
        {
            var msgs = await messages.GetRecentByChannelAsync(channelId, limit);
            msgs.Reverse();
            return msgs;
        }
    }
}
