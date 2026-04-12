using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;

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

        /// <summary>同一用户连续消息归入同一话题的时间窗口</summary>
        private readonly TimeSpan continuationWindow = TimeSpan.FromMinutes(5);

        /// <summary>话题超时时间，超过此时间未活跃的话题自动关闭</summary>
        private readonly TimeSpan topicTimeout = TimeSpan.FromHours(2);

        /// <summary>获取上下文时默认返回的最近消息数量</summary>
        private const int DefaultContextLimit = 20;

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

            // 5. 话题归类（规则层）
            var topic = await ClassifyTopicAsync(user, channel, msg);

            // 6. 更新话题最后活跃时间
            topic.LastMessageTime = msg.Time;
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

            // 8. 获取最近历史消息
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

        /// <summary>
        /// 规则层话题归类。
        /// 策略：回复引用 → 同用户短时间连续 → 新建话题。
        /// TODO: 规则层无法判定时，接入模型辅助层做语义分类。
        /// </summary>
        private async Task<Topic> ClassifyTopicAsync(User user, Channel channel, IncomingMessage msg)
        {
            // 规则 1：回复/引用关系 → 归入被回复消息所属的 Topic
            if (!string.IsNullOrEmpty(msg.ReplyTo) && int.TryParse(msg.ReplyTo, out var replyMsgId))
            {
                var replyMsg = await messages.GetByIdAsync(replyMsgId);
                if (replyMsg != null)
                {
                    var replyTopic = await topics.GetByIdAsync(replyMsg.TopicId);
                    if (replyTopic != null && replyTopic.IsActive)
                        return replyTopic;
                }
            }

            // 规则 2：同一用户在同一频道、短时间内的连续消息 → 归入最近活跃的 Topic
            var activeTopics = await topics.GetActiveByChannelAsync(channel.Id);
            if (activeTopics.Count > 0)
            {
                // 查找该用户最近参与的话题
                var cutoff = msg.Time - continuationWindow;
                foreach (var t in activeTopics)
                {
                    if (t.LastMessageTime >= cutoff)
                    {
                        // 检查该话题中是否有该用户的最近消息
                        var recent = await messages.GetRecentByTopicAsync(t.Id, 5);
                        if (recent.Any(m => m.UserId == user.Id))
                            return t;
                    }
                }
            }

            // 规则 3：无法匹配 → 创建新话题
            // 用消息内容的前 20 个字符作为话题名称
            var topicName = msg.Content.Length > 20
                ? msg.Content.Substring(0, 20) + "..."
                : msg.Content;
            return await topics.CreateAsync(channel.Id, topicName);
        }
    }
}
