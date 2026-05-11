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
    /// 睡眠期间拦截消息：入库但不触发响应。
    /// </summary>
    internal class ChannelEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Channel";

        private SessionContext? pendingContext;
        private IncomingMessage? pendingMessage;

        /// <summary>活跃频道引擎表（ChannelId → ChannelEngine）。</summary>
        private readonly Dictionary<int, ChannelEngine> activeChannels = new();

        /// <summary>暂存通知（频道循环不活跃时暂存，下次创建时注入）。</summary>
        private readonly Dictionary<int, List<string>> pendingNotifications = new();

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
                    FrameworkLogger.LogPermission("WorkerSpawnCheck", sessionContext.User.PlatformId, "Blocked", false);
                    return false;
                case PermissionLevel.Restricted:
                    FrameworkLogger.LogPermission("WorkerSpawnCheck", sessionContext.User.PlatformId, "Restricted", false);
                    return false;
            }

            // ═══ 睡眠拦截 ═══
            var sleepState = ctx.CurrentSleepState;
            if (sleepState != SleepState.None)
            {
                // 走神：被 @ 放行（DreamEngine 会自行打断）
                if (sleepState == SleepState.Daydream && message.IsMentioned)
                {
                    // 放行，走正常流程
                }
                // 小睡：被 @ + 叫醒关键词 → 放行
                else if (sleepState == SleepState.Nap
                    && message.IsMentioned
                    && ContainsWakeKeyword(message.Content))
                {
                    // 放行（DreamEngine 会自行打断）
                }
                // 大睡：仅管理员 + @ 放行
                else if (sleepState == SleepState.DeepSleep
                    && message.IsMentioned
                    && sessionContext.User.PermissionLevel >= PermissionLevel.Admin)
                {
                    // 管理员叫醒 → 发信号唤醒 DreamEngine，放行消息
                    ctx.EventBus.Publish(new SignalEvent
                    {
                        SignalName = "force-wake",
                        Payload = "admin-wake"
                    });
                }
                else
                {
                    // 其余情况：消息已入库，不触发响应
                    return false;
                }
            }

            // 信任等级：首次出现自动升为 Stranger
            if (sessionContext.Person.TrustLevel == TrustLevel.Unknown)
            {
                sessionContext.Person.TrustLevel = TrustLevel.Stranger;
                await ctx.Session.UpdatePersonAsync(sessionContext.Person);
            }

            var channelId = sessionContext.Channel.Id;

            // 已有活跃的频道引擎 → 转发消息
            if (activeChannels.TryGetValue(channelId, out var existing) && existing.IsAlive)
            {
                existing.EnqueueMessage(message, sessionContext);
                return false;
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

            // 注入暂存的系统通知
            if (pendingNotifications.TryGetValue(sc.Channel.Id, out var notifications))
            {
                foreach (var n in notifications)
                    engine.InjectNotification(n);
                pendingNotifications.Remove(sc.Channel.Id);
            }

            return engine;
        }

        internal IReadOnlyDictionary<int, ChannelEngine> GetActiveChannels() => activeChannels;

        /// <summary>暂存通知（频道循环不活跃时调用）。</summary>
        internal void StashNotification(int channelId, string content)
        {
            if (!pendingNotifications.TryGetValue(channelId, out var list))
            {
                list = new List<string>();
                pendingNotifications[channelId] = list;
            }
            list.Add(content);
        }

        private static readonly string[] WakeKeywords =
            ["起床", "醒醒", "wake", "起来", "叫醒", "别睡了", "醒来"];

        private static bool ContainsWakeKeyword(string content)
        {
            var lower = content.ToLowerInvariant();
            return WakeKeywords.Any(k => lower.Contains(k));
        }
    }
}
