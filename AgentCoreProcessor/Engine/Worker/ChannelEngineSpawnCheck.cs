using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// ChannelEngine 的创建条件检查。接管 SessionManager 调用和频道路由。
    /// 维护活跃频道引擎表，按 ChannelId 路由消息。
    /// 睡眠行为由 ChannelEngine 内部处理，此处不拦截。
    /// </summary>
    internal class ChannelEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Channel";

        /// <summary>活跃频道引擎表（ChannelId → ChannelEngine）。</summary>
        private readonly Dictionary<int, ChannelEngine> activeChannels = new();

        /// <summary>保护 ShouldSpawn→Create 序列的并发安全。</summary>
        private readonly object _spawnLock = new();
        /// <summary>正在创建中的频道 ID 集合，防止并发重复创建。</summary>
        private readonly HashSet<int> _pendingSpawns = new();

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            // 清理已死亡的频道引擎
            lock (_spawnLock)
            {
                var dead = activeChannels.Where(kv => !kv.Value.IsAlive).Select(kv => kv.Key).ToList();
                foreach (var key in dead)
                {
                    activeChannels.Remove(key);
                    _pendingSpawns.Remove(key);
                }
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// 直接消息路由入口（绕过 EventBus）。处理消息入库 + 频道引擎孵化 / 转发。
        /// </summary>
        public async Task HandleMessageAsync(IncomingMessage msg, ISystemContext ctx)
        {
            // 清理已死亡的频道引擎
            lock (_spawnLock)
            {
                var dead = activeChannels.Where(kv => !kv.Value.IsAlive).Select(kv => kv.Key).ToList();
                foreach (var key in dead)
                {
                    activeChannels.Remove(key);
                    _pendingSpawns.Remove(key);
                }
            }

            // SessionManager：用户映射、频道、消息入库
            var sessionContext = await ctx.Session.OnMessageAsync(msg);

            // 自产消息（bot 自身行为触发的 notice 回执）：已入库但不触发引擎孵化
            if (msg.IsSelfTriggered) return;

            // 权限检查
            switch (sessionContext.User.PermissionLevel)
            {
                case PermissionLevel.Blocked:
                    return;
                case PermissionLevel.Restricted:
                    return;
            }

            var channelId = sessionContext.Channel.Id;

            // 信任等级：首次出现自动升为 Stranger
            if (sessionContext.Person.TrustLevel == TrustLevel.Unknown)
            {
                sessionContext.Person.TrustLevel = TrustLevel.Stranger;
                await ctx.Session.UpdatePersonAsync(sessionContext.Person);
            }

            // 入向接通：频道被 session full-claim → 改投 session 端口，不碰 ChannelEngine。
            // 消息已由 OnMessageAsync 完整入库+身份映射，这里只分发已入库的 UserMessage。
            var sessionEngine = ctx.GetActiveEnginesSnapshot()
                .OfType<Session.SessionEngine>().FirstOrDefault();
            if (sessionEngine != null && sessionContext.IncomingMessage != null)
            {
                var claimerId = sessionEngine.GetFullClaimer(channelId);
                if (claimerId != null && sessionEngine.GetSession(claimerId)?.IsAlive == true)
                {
                    var messageEngine = ctx.GetActiveEnginesSnapshot()
                        .OfType<Messaging.MessageEngine>().FirstOrDefault();
                    if (messageEngine != null)
                    {
                        messageEngine.DeliverToSession(channelId, sessionContext.IncomingMessage);
                        return;
                    }
                }
            }

            ISubEngine? newEngine = null;

            lock (_spawnLock)
            {
                // 已有活跃的频道引擎 → 无条件转发
                if (activeChannels.TryGetValue(channelId, out var existing) && existing.IsAlive)
                {
                    existing.EnqueueMessage(msg, sessionContext, null);
                    return;
                }

                // 已有并发流程正在为此频道创建引擎 → 不重复创建
                if (!_pendingSpawns.Add(channelId))
                    return;

                // 创建新引擎
                var engine = new ChannelEngine(ctx, sessionContext, msg);
                activeChannels[channelId] = engine;
                _pendingSpawns.Remove(channelId);
                newEngine = engine;
            }

            // 在锁外启动引擎
            if (newEngine != null)
                ctx.StartEngine(newEngine);
        }

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            // 消息路由已迁移至 HandleMessageAsync，不再通过 EventBus 孵化
            return Task.FromResult(false);
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            // 不再通过 EventBus 触发，Create 备用（TryColdStart 仍然使用）
            throw new System.InvalidOperationException("ChannelEngine should be created via HandleMessageAsync or TryColdStart");
        }

        internal IReadOnlyDictionary<int, ChannelEngine> GetActiveChannels() => activeChannels;

        /// <summary>冷启动频道引擎（无消息触发）。返回 null 表示引擎已活跃。</summary>
        internal ChannelEngine? TryColdStart(Database.Channel channel, ISystemContext ctx)
        {
            lock (_spawnLock)
            {
                if (activeChannels.TryGetValue(channel.Id, out var existing) && existing.IsAlive)
                    return null;

                if (!_pendingSpawns.Add(channel.Id))
                    return null;
            }

            var engine = new ChannelEngine(ctx, channel);
            activeChannels[channel.Id] = engine;
            _pendingSpawns.Remove(channel.Id);
            return engine;
        }

    }
}
