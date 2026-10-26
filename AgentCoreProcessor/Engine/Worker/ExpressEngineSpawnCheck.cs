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
            // ① 骨架：入库 + 身份映射。顺序硬约束——先落库，再决策。
            var sessionContext = await ctx.Session.OnMessageAsync(msg);

            // 自产消息（bot 自身行为触发的 notice 回执）：已入库但不触发应答
            if (msg.IsSelfTriggered) return;

            // ② 骨架：权限门。被屏蔽/受限的发送者不进入任何应答路径（图也只能在门后运行）。
            switch (sessionContext.User.PermissionLevel)
            {
                case PermissionLevel.Blocked:
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

            // ③ 骨架：伴随提取入批。与"要不要应答"无关，挂了图的频道也照常入批。
            EnqueueCompanionExtraction(sessionContext, ctx);

            // 同一频道同一条消息只投递一次：账本与执行器都由骨架持有。
            // 键在**成功之后**才占用——失败必须留出给兜底图重试的机会。
            var deliveredKeys = new HashSet<string>(StringComparer.Ordinal);
            var traceId = msg.PlatformMessageId ?? $"{msg.Platform}:{msg.ChannelId}";
            var messageEvent = Graph.KeywordGraphRunner.ToEvent(msg, channelId);
            var executor = new Graph.IngressIntentExecutor(
                deliveredKeys,
                deliveryKey: $"{channelId}:{sessionContext.IncomingMessage?.Id}",
                deliverDefault: DeliverToChannelSessionAsync,
                route: RouteToSessionAsync,
                send: DispatchSendAsync);

            // ④ 插槽：挂了图的频道由图产出意图，由骨架执行。
            //    图没有返回值——每张图各自跑完就待命，要不要投递只看它们产出的意图。
            //    active：图接管本次消息，不再走默认会话路径；shadow：只记录意图，行为不变。
            var graphs = Graphs(ctx);
            var mountedRunners = graphs?.RunnersFor(channelId) ?? Array.Empty<Graph.KeywordGraphRunner>();
            if (mountedRunners.Count > 0)
            {
                var intents = new List<AgentLilara.GraphRuntime.Graph.ActionIntent>();
                foreach (var runner in mountedRunners)
                {
                    intents.AddRange(await runner.EvaluateAsync(messageEvent, traceId));
                }

                if (graphs!.IsActive)
                {
                    var failures = await executor.ExecuteAsync(intents);

                    Signal.Event(LogGroup.Engine, "图接管本次消息路由",
                        new
                        {
                            channelId,
                            msgId = sessionContext.IncomingMessage?.Id,
                            graphs = mountedRunners.Count,
                            intents = intents.Count,
                            failures = failures.Count
                        });

                    // 失败是信息：派生一条错误事件交给兜底图（只一跳）。
                    if (failures.Count > 0)
                        await RunFallbackAsync(failures);

                    return;
                }
            }

            // ⑤ 骨架：默认路径 —— 复用频道活跃会话，空闲则孵化（未挂图或 shadow 模式）。
            var defaultFailure = await DeliverToChannelSessionAsync();
            if (defaultFailure != null)
                await RunFallbackAsync(new[] { defaultFailure });

            // —— 骨架侧的执行器：动作怎么执行、失败怎么记 ——

            async Task<string?> DispatchSendAsync(AgentLilara.GraphRuntime.Graph.ActionIntent intent)
            {
                try
                {
                    await new Graph.MessageGraphIntentDispatcher(
                        outgoing => ctx.Adapters.SendMessageAsync(msg.Platform, outgoing),
                        channelId).DispatchAsync(intent);
                    return null;
                }
                catch (Exception ex)
                {
                    // 出向失败不影响已完成的入库与后续意图
                    Signal.Warn(LogGroup.Engine, "graph.send.failed",
                        new { channelId, error = ex.Message });
                    return $"send-failed:{ex.Message}";
                }
            }

            // 路由到频道会话（去重与记账由执行器负责，这里只做一次尝试）。
            async Task<string?> RouteToSessionAsync(string sessionType)
            {
                if (sessionContext.IncomingMessage == null) return "message-missing";

                var sessionEngine = ctx.GetActiveEnginesSnapshot()
                    .OfType<Session.SessionEngine>().FirstOrDefault();
                if (sessionEngine == null)
                {
                    Signal.Warn(LogGroup.Engine, "graph.route.failed",
                        new { channelId, reason = "session-engine-unavailable", sessionType });
                    return "session-engine-unavailable";
                }

                await sessionEngine.WaitRestoredAsync(TimeSpan.FromSeconds(5));
                if (!sessionEngine.RouteGraphMessage(
                        channelId,
                        sessionType,
                        sessionContext.IncomingMessage,
                        msg.IsMentioned || msg.IsPrivate))
                {
                    Signal.Warn(LogGroup.Engine, "graph.route.failed",
                        new { channelId, reason = "route-rejected", sessionType });
                    return $"route-rejected:{sessionType}";
                }

                return null;
            }

            // 默认路径：复用频道活跃会话；空闲时按默认会话类型孵化，再投递本条消息。
            // 返回 null = 已投递；否则返回失败原因。去重与记账由执行器负责。
            async Task<string?> DeliverToChannelSessionAsync()
            {
                if (sessionContext.IncomingMessage == null) return null;

                var sessionEngine = ctx.GetActiveEnginesSnapshot()
                    .OfType<Session.SessionEngine>().FirstOrDefault();
                if (sessionEngine == null) return "session-engine-unavailable";

                // 冷启动时序：SessionEngine 经 Task.Run 启动，RestoreAll 在 RunAsync 开头执行；
                // 先等恢复完成，避免恢复的 claim 尚未落地就被当成"频道空闲"。
                await sessionEngine.WaitRestoredAsync(TimeSpan.FromSeconds(5));

                var messageEngine = ctx.GetActiveEnginesSnapshot()
                    .OfType<Messaging.MessageEngine>().FirstOrDefault();
                if (messageEngine == null) return "message-engine-unavailable";

                // 私聊视同提及：投 mention 子流即时激活（normal 子流要攒满权重才醒）。
                var asMention = msg.IsMentioned || msg.IsPrivate;
                var claimerId = sessionEngine.GetFullClaimer(channelId);
                var claimerAlive = claimerId != null && sessionEngine.GetSession(claimerId)?.IsAlive == true;
                Signal.Debug(LogGroup.Engine, "入向投递判定",
                    new { channelId, msgId = sessionContext.IncomingMessage.Id, claimer = claimerId, alive = claimerAlive });

                if (claimerId != null && claimerAlive)
                {
                    messageEngine.DeliverToSession(channelId, sessionContext.IncomingMessage, asMention);
                    Signal.Event(LogGroup.Engine, "入向已投递到 session 端口",
                        new { channelId, msgId = sessionContext.IncomingMessage.Id, sessionId = claimerId, asMention });
                    return null;
                }

                var created = sessionEngine.CreateExpressSessionForChannel(channelId);
                if (created == null)
                {
                    Signal.Warn(LogGroup.Engine, "会话创建失败或频道已被占用", new { channelId });
                    return "session-create-failed";
                }

                messageEngine.DeliverToSession(channelId, sessionContext.IncomingMessage, asMention);
                Signal.Event(LogGroup.Engine, "会话孵化 + 投递",
                    new { channelId, msgId = sessionContext.IncomingMessage.Id, sessionId = created.SessionId, asMention });
                return null;
            }

            // 兜底：把失败变成一条错误事件，交给本频道（或 global）的错误图跑一跳。
            // 层级规则由 GraphFallbackRunner 负责：到顶只审计，不再派生新事件。
            async Task RunFallbackAsync(IReadOnlyList<string> failures)
            {
                var errorRunners = graphs?.RunnersForError(channelId) ?? Array.Empty<Graph.KeywordGraphRunner>();
                if (errorRunners.Count == 0)
                {
                    Signal.Warn(LogGroup.Engine, "graph.error.unhandled",
                        new { channelId, msgId = sessionContext.IncomingMessage?.Id, failures });
                    return;
                }

                var fallback = new Graph.GraphFallbackRunner(
                    evaluate: async (depth, reasons) =>
                    {
                        var errorEvent = AgentLilara.GraphRuntime.Contracts.InboundEvent.RuntimeError(
                            channelId.ToString(),
                            string.Join(" | ", reasons),
                            dedupeKey: messageEvent.DedupeKey,
                            depth: depth);
                        var errorTraceId = $"{traceId}:error:{depth}";

                        var errorIntents = new List<AgentLilara.GraphRuntime.Graph.ActionIntent>();
                        foreach (var runner in errorRunners)
                        {
                            errorIntents.AddRange(await runner.EvaluateAsync(errorEvent, errorTraceId));
                        }

                        return errorIntents;
                    },
                    execute: executor.ExecuteAsync);

                var result = await fallback.RunAsync(failures);

                if (result.Failures.Count > 0)
                {
                    Signal.Warn(LogGroup.Engine, "graph.error.fallback_failed",
                        new { channelId, depth = result.Depth, failures = result.Failures });
                }

                Signal.Event(LogGroup.Engine, "图兜底已执行",
                    new
                    {
                        channelId,
                        depth = result.Depth,
                        attempted = result.Attempted,
                        graphs = errorRunners.Count,
                        failures = result.Failures.Count
                    });
            }
        }

        /// <summary>
        /// 伴随提取入批（骨架侧，与"要不要应答"无关）：达攒批阈值时内部会跑一轮 LLM 调用，
        /// 故 fire-and-forget，不阻塞消息链路。挂了图的频道也照常入批。
        /// </summary>
        private static void EnqueueCompanionExtraction(SessionContext sessionContext, ISystemContext ctx)
        {
            if (sessionContext.IncomingMessage == null) return;

            var mind = ctx.GetActiveEnginesSnapshot()
                .OfType<Mind.MindEngine>().FirstOrDefault();
            var extractor = mind?.Extract.Express;
            if (extractor == null) return;

            var senderName = !string.IsNullOrWhiteSpace(sessionContext.User.DisplayName)
                ? sessionContext.User.DisplayName
                : "用户";
            _ = extractor.AddMessageAsync(
                sessionContext.Channel.Id.ToString(), senderName, sessionContext.IncomingMessage.Content);
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
