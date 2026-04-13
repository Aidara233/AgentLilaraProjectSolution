using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 话题引擎的创建条件检查。接管 SessionManager 调用和话题路由。
    /// 维护活跃话题引擎表，按 TopicId 路由消息。
    /// </summary>
    internal class TopicEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Topic";

        private SessionContext? pendingContext;
        private IncomingMessage? pendingMessage;

        /// <summary>活跃话题引擎表（TopicId → TopicEngine）。</summary>
        private readonly Dictionary<int, TopicEngine> activeTopics = new();

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            // 清理已死亡的话题引擎
            var dead = activeTopics.Where(kv => !kv.Value.IsAlive).Select(kv => kv.Key).ToList();
            foreach (var key in dead) activeTopics.Remove(key);
            return Task.CompletedTask;
        }

        public async Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            if (e is not MessageEvent msgEvent) return false;

            var message = msgEvent.Message;

            // SessionManager：用户映射、频道、话题分类、消息入库
            var sessionContext = await ctx.Session.OnMessageAsync(message);

            // 权限检查
            switch (sessionContext.User.PermissionLevel)
            {
                case PermissionLevel.Blocked:
                    FrameworkLogger.LogPermission("TopicSpawnCheck", sessionContext.User.PlatformId, "Blocked", false);
                    return false;
                case PermissionLevel.Restricted:
                    FrameworkLogger.LogPermission("TopicSpawnCheck", sessionContext.User.PlatformId, "Restricted", false);
                    return false;
            }
            var topicId = sessionContext.Topic.Id;

            // 已有活跃的话题引擎 → 转发消息
            if (activeTopics.TryGetValue(topicId, out var existing) && existing.IsAlive)
            {
                existing.EnqueueMessage(message, sessionContext);
                return false;
            }

            // 需要创建新的话题引擎
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

            var engine = new TopicEngine(ctx, sc, msg);
            activeTopics[sc.Topic.Id] = engine;
            return engine;
        }
    }
}
