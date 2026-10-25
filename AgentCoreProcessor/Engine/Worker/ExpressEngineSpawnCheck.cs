using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// ExpressEngineSpawnCheck 的创建条件检查。接管 SessionManager 调用和频道路由。
    /// express 会话化后：无占用频道消息 → 创建 Type="express" 会话（AutoClaim Full）承载响应，
    /// 不再创建 ExpressEngine（引擎）。睡眠/禁言行为由 express 会话承接（后续阶段）。
    /// </summary>
    internal class ExpressEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Express";

        /// <summary>图挂载表由宿主提供；这里不再自持（生命周期跟随 MasterEngine）。</summary>
        private Graph.GraphManager? Graphs(ISystemContext ctx) => ctx.Graphs;

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// 直接消息路由入口（绕过 EventBus）。处理消息入库 + 频道占用判定 / express 会话孵化。
        /// </summary>
        public async Task HandleMessageAsync(IncomingMessage msg, ISystemContext ctx)
        {
            // SessionManager：用户映射、频道、消息入库
            var sessionContext = await ctx.Session.OnMessageAsync(msg);

            // 自产消息（bot 自身行为触发的 notice 回执）：已入库但不触发孵化
            if (msg.IsSelfTriggered) return;

            // 入站图（挂载表）：消息已入库；**未挂载的频道完全不干预**。
            // active 模式下图说“不激活”则跳过会话路径；shadow 模式只记录结论。
            // 出向走 AdapterManager（不走 MessageEngine.OnOutgoingReady —— 那条无人订阅）。
            var graphChannelId = sessionContext.Channel.Id;
            var graphs = Graphs(ctx);
            var mountedGraph = graphs?.For(graphChannelId);
            if (mountedGraph != null)
            {
                var allow = await mountedGraph.DecideAsync(
                    Graph.KeywordGraphRunner.ToEvent(msg, graphChannelId),
                    msg.PlatformMessageId ?? $"{msg.Platform}:{msg.ChannelId}",
                    intent => new Graph.MessageGraphIntentDispatcher(
                        outgoing => ctx.Adapters.SendMessageAsync(msg.Platform, outgoing),
                        graphChannelId).DispatchAsync(intent));

                if (graphs!.IsActive && !allow)
                {
                    Signal.Event(LogGroup.Engine, "图决定不激活",
                        new { channelId = graphChannelId, platformMessageId = msg.PlatformMessageId });
                    return;
                }
            }

            // 伴随提取入批（入向唯一咽喉，覆盖 express 会话与 session 占用两条路）。
            // 达攒批阈值时内部会跑一轮 LLM 调用，故 fire-and-forget 不阻塞消息链路。
            if (sessionContext.IncomingMessage != null)
            {
                var mind = ctx.GetActiveEnginesSnapshot()
                    .OfType<Mind.MindEngine>().FirstOrDefault();
                var extractor = mind?.Extract.Express;
                if (extractor != null)
                {
                    var senderName = !string.IsNullOrWhiteSpace(sessionContext.User.DisplayName)
                        ? sessionContext.User.DisplayName
                        : "用户";
                    var channelKey = sessionContext.Channel.Id.ToString();
                    var content = sessionContext.IncomingMessage.Content;
                    _ = extractor.AddMessageAsync(channelKey, senderName, content);
                }
            }

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

            // 入向接通：频道被 session full-claim → 改投 session 端口，不孵化新会话。
            // 消息已由 OnMessageAsync 完整入库+身份映射，这里只分发已入库的 UserMessage。
            //
            // ⚠ 冷启动时序：SessionEngine 通过 Task.Run 启动，RestoreAll 在 RunAsync 开头执行。
            // 如果消息在 RestoreAll 完成前到达，claim 字典还是空的，消息会错误地走 express 孵化。
            // 因此先 await WaitRestoredAsync 确保 claim 恢复完成（带 5s 超时兜底）。
            var sessionEngine = ctx.GetActiveEnginesSnapshot()
                .OfType<Session.SessionEngine>().FirstOrDefault();
            if (sessionEngine == null || sessionContext.IncomingMessage == null)
                return;

            // 等待 RestoreAll 完成，确保冷启动恢复的 claim 已落地
            await sessionEngine.WaitRestoredAsync(TimeSpan.FromSeconds(5));

            var messageEngine = ctx.GetActiveEnginesSnapshot()
                .OfType<Messaging.MessageEngine>().FirstOrDefault();
            if (messageEngine == null) return;

            var claimerId = sessionEngine.GetFullClaimer(channelId);
            var claimerAlive = claimerId != null && sessionEngine.GetSession(claimerId)?.IsAlive == true;
            Signal.Debug(LogGroup.Engine, "入向投递判定",
                new { channelId, msgId = sessionContext.IncomingMessage.Id, claimer = claimerId, alive = claimerAlive });
            if (claimerId != null && claimerAlive)
            {
                // 私聊视同提及：投 mention 子流即时激活 session（否则 normal 子流要攒满 20 条权重才醒）。
                // express 会话与 working 会话同走此路径（DeliverToSession → 端口权重激活）。
                var deliverAsMention = msg.IsMentioned || msg.IsPrivate;
                messageEngine.DeliverToSession(channelId, sessionContext.IncomingMessage, deliverAsMention);
                Signal.Event(LogGroup.Engine, "入向已投递到 session 端口",
                    new { channelId, msgId = sessionContext.IncomingMessage.Id, sessionId = claimerId, asMention = deliverAsMention });
                return;
            }

            // 无占用 → 孵化 express 会话（AutoClaim Full 接管频道），再投递本条消息。
            // express 会话按端口权重激活（mention 立即 / normal 攒 20 条 / 私聊视同提及）。
            var expressSession = sessionEngine.CreateExpressSessionForChannel(channelId);
            if (expressSession == null)
            {
                Signal.Warn(LogGroup.Engine, "express 会话创建失败或频道已被占用", new { channelId });
                return;
            }
            var expressDeliverAsMention = msg.IsMentioned || msg.IsPrivate;
            messageEngine.DeliverToSession(channelId, sessionContext.IncomingMessage, expressDeliverAsMention);
            Signal.Event(LogGroup.Engine, "express 会话孵化 + 投递",
                new { channelId, msgId = sessionContext.IncomingMessage.Id, sessionId = expressSession.SessionId, asMention = expressDeliverAsMention });
        }

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            // 消息路由已迁移至 HandleMessageAsync，不再通过 EventBus 孵化
            return Task.FromResult(false);
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            // 不再通过 EventBus 触发，Create 备用（TryColdStart 已随引擎移除）
            throw new System.InvalidOperationException("express 会话化后不再创建 ExpressEngine，消息经 HandleMessageAsync 孵化 express 会话");
        }
    }
}
