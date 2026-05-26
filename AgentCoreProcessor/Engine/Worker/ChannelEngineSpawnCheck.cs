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

        private SessionContext? pendingContext;
        private IncomingMessage? pendingMessage;

        /// <summary>活跃频道引擎表（ChannelId → ChannelEngine）。</summary>
        private readonly Dictionary<int, ChannelEngine> activeChannels = new();

        // 频道循环不活跃时的跨循环请求暂存（待实现：DelegationBus 自动路由）

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            // 清理已死亡的频道引擎
            var dead = activeChannels.Where(kv => !kv.Value.IsAlive).Select(kv => kv.Key).ToList();
            foreach (var key in dead) activeChannels.Remove(key);
            return Task.CompletedTask;
        }

        public async Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            if (e is not MessageEvent msgEvent) return false;

            var message = msgEvent.Message;

            // SessionManager：用户映射、频道、消息入库（无论是否睡眠都要入库）
            var sessionContext = await ctx.Session.OnMessageAsync(message);

            // 权限检查
            switch (sessionContext.User.PermissionLevel)
            {
                case PermissionLevel.Blocked:
                    return false;
                case PermissionLevel.Restricted:
                    return false;
            }

            var channelId = sessionContext.Channel.Id;

            // 已有活跃的频道引擎 → 无条件转发
            if (activeChannels.TryGetValue(channelId, out var existing) && existing.IsAlive)
            {
                existing.EnqueueMessage(message, sessionContext, e.TraceParentSpanId);
                return false;
            }

            // 信任等级：首次出现自动升为 Stranger
            if (sessionContext.Person.TrustLevel == TrustLevel.Unknown)
            {
                sessionContext.Person.TrustLevel = TrustLevel.Stranger;
                await ctx.Session.UpdatePersonAsync(sessionContext.Person);
            }

            // 需要创建新的频道引擎
            pendingContext = sessionContext;
            pendingMessage = message;
            return true;
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            var sc = pendingContext!;
            var msg = pendingMessage!;
            pendingContext = null;
            pendingMessage = null;

            var engine = new ChannelEngine(ctx, sc, msg);
            activeChannels[sc.Channel.Id] = engine;
            return engine;
        }

        internal IReadOnlyDictionary<int, ChannelEngine> GetActiveChannels() => activeChannels;

        /// <summary>冷启动频道引擎（无消息触发）。返回 null 表示引擎已活跃。</summary>
        internal ChannelEngine? TryColdStart(Database.Channel channel, ISystemContext ctx)
        {
            if (activeChannels.TryGetValue(channel.Id, out var existing) && existing.IsAlive)
                return null;

            var engine = new ChannelEngine(ctx, channel);
            activeChannels[channel.Id] = engine;
            return engine;
        }

    }
}
