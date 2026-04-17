using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 会话管理器，负责用户/频道映射、消息入库。
    /// </summary>
    internal class SessionManager
    {
        private readonly UserRepository users;
        private readonly PersonRepository persons;
        private readonly ChannelRepository channels;
        private readonly MessageRepository messages;

        /// <summary>获取上下文时默认返回的最近消息数量</summary>
        private const int DefaultContextLimit = 20;

        public SessionManager(
            UserRepository users,
            PersonRepository persons,
            ChannelRepository channels,
            MessageRepository messages)
        {
            this.users = users;
            this.persons = persons;
            this.channels = channels;
            this.messages = messages;
        }

        /// <summary>
        /// 轻量用户解析：只做平台用户映射和 Person 查询。
        /// 不进行频道映射、消息入库。用于命令系统等不需要完整会话管道的场景。
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

            // 4. 消息入库
            var senderName = !string.IsNullOrEmpty(user.DisplayName) ? user.DisplayName
                           : !string.IsNullOrEmpty(msg.DisplayName) ? msg.DisplayName
                           : msg.PlatformUserId;
            var userMessage = new UserMessage
            {
                UserId = user.Id,
                ChannelId = channel.Id,
                Content = msg.Content,
                SenderName = senderName,
                Time = msg.Time,
                PlatformMessageId = msg.PlatformMessageId,
                ImageCount = msg.Attachments?.Count(a => a.Type == AttachmentType.Image) ?? 0,
                ReplyToPlatformMessageId = msg.ReplyTo,
                MentionedPlatformIds = msg.MentionedPlatformIds != null && msg.MentionedPlatformIds.Count > 0
                    ? string.Join(",", msg.MentionedPlatformIds)
                    : null
            };
            await messages.SaveAsync(userMessage);

            // 5. 获取最近历史消息（按频道）
            var recent = await GetContextByChannelAsync(channel.Id);

            return new SessionContext
            {
                User = user,
                Person = person,
                Channel = channel,
                RecentMessages = recent
            };
        }

        /// <summary>保存 Lilara 的回复到消息历史。</summary>
        public async Task SaveBotMessageAsync(int channelId, string content, string? platformMessageId = null)
        {
            await messages.SaveAsync(new UserMessage
            {
                UserId = 0,
                ChannelId = channelId,
                Content = content,
                SenderName = "Lilara",
                IsFromBot = true,
                Time = DateTime.Now,
                PlatformMessageId = platformMessageId
            });
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

        /// <summary>获取指定频道的最近历史消息（按时间升序）。</summary>
        public async Task<List<UserMessage>> GetContextByChannelAsync(int channelId, int limit = DefaultContextLimit)
        {
            var msgs = await messages.GetRecentByChannelAsync(channelId, limit);
            msgs.Reverse();
            return msgs;
        }

        /// <summary>按平台消息ID查找消息（用于引用上下文）。</summary>
        public Task<UserMessage?> GetByPlatformMessageIdAsync(int channelId, string platformMessageId)
            => messages.GetByPlatformMessageIdAsync(channelId, platformMessageId);

        /// <summary>以某条消息为锚点，取前后各 radius 条消息作为上下文。</summary>
        public Task<List<UserMessage>> GetContextAroundAsync(int messageId, int channelId, int radius = 3)
            => messages.GetContextAroundAsync(messageId, channelId, radius);
    }
}
