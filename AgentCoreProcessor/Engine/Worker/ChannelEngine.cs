using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Memory;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Engine.Vision;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Tool.Core;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 频道引擎。长生命周期，一个活跃频道一个实例。
    /// 负责消息缓冲聚合、冲动值决策、参与者追踪、消息处理（分类→记忆→回复→提取）。
    /// </summary>
    internal partial class ChannelEngine : ISubEngine, IAgentHost
    {
        public string EngineType => "Channel";
        public bool IsAlive { get; private set; } = true;

        /// <summary>频道 ID（公开）。</summary>
        public int ChannelId => channelId;

        /// <summary>是否正在处理消息。</summary>
        public bool IsBusy => Interlocked.Read(ref _busyFlag) == 1;

        /// <summary>上次处理完成的时间。用于冷却期计算。</summary>
        public DateTime? LastCompletionTime
        {
            get
            {
                var ticks = Interlocked.Read(ref _completionTicks);
                return ticks == 0 ? null : new DateTime(ticks);
            }
        }

        // ---- 内部状态 ----

        private readonly ISystemContext ctx;
        private readonly int channelId;
        private readonly string channelName;
        private readonly ChannelConfig channelConfig;
        private long _busyFlag = 0;
        private long _completionTicks = 0;
        private int _totalGateCycles = 0;
        private int _consecutiveExpressFailures = 0;

        // ---- 禁言状态 ----
        private bool _isBanned; // 当前是否被禁言，禁言期间不唤醒引擎

        // 冲动值
        private readonly ImpulseTracker impulseTracker;
        /// <summary>上次 GetParticipantsAsync 返回的参与者数。同步 Accumulate 调用的 fallback。初始为 1。</summary>
        private int _lastKnownParticipantCount = 1;

        // 上下文服务（DB 派生，无累积状态）
        private ChannelContextState _ctxState = null!;
        private ChannelContextService _ctxService = null!;

        // Core 实例
        private readonly AgentCore agentCore = new();

        // ── 统一循环（Phase 1）──
        private Gate gate = null!;
        private AgentConfig agentConfig = null!;
        private ChannelContextPersistence? persistence;

        // ── 堆叠式上下文 ──
        private string? fixedPrefix;

        // 事件总线 + 内务模块
        private readonly LoopBus bus = new();
        private readonly LoopControlModule loopControlModule = new();
        private readonly DelegationNotificationModule delegationNotificationModule = new();
        private List<EngineModule> modules = null!;

        // 已处理消息标记
        private readonly LinkedList<long> processedTicks = new();
        private const int MaxProcessedTicksWindow = 50;

        // 跨循环请求队列
        private readonly ConcurrentQueue<LoopMessage> _pendingLoopMessages = new();
        internal IAgentMessaging? _messaging;
        private SignalFilterConfig _signalFilter = new();


        // 记忆提取计数（用于退出时判断是否需要收尾提取）
        private int processedMessageCount = 0;
        private int unrespondedMessageCount = 0;

        // Express 对话历史：从数据库拉取最近 N 条消息
        private const int HistoryMaxMessages = 20;

        // TrustProgress 每日自动增长跟踪
        private readonly Dictionary<int, (DateTime Date, float Accumulated)> dailyProgressTracker = new();

        // StartInject 已消费的最大消息 Id（防止 BuildRoundInjectAsync 重复拉取）
        private int _startInjectMaxId;

        // Express/Working 自适应切换 — 已移除 Working 模式，仅保留 Express

        // 本轮触发是否因 @提及（用于 impulse 额外扣减）
        private bool _triggerHadMention;

        // 梦话系统
        private readonly SleepTalkCore sleepTalkCore = new();
        private volatile bool _sleepTalkMode;
        private volatile bool _justWokenUp;

        // 模式配置驱动（Phase 2）：当前模式 ID 和定义 — 已移除，仅保留 Express

        // 统一游标：最后消费消息 DB Id
        private int _lastConsumedMessageId;

        // ── 统一循环 Phase 2：双源注入 ──
        private readonly List<IInjectProvider> _injectProviders = new();

        // 当前处理批次（供 Agent host 注入使用）
        private SessionContext? _lastSessionContext;
        private bool hadSpeakThisRound;

        // 错误追踪
        private DateTime? lastErrorTime = null;
        private string? lastErrorMessage = null;
        private int totalErrorCount = 0;

        // 信号追踪：最近入队消息携带的上游信号
        private string? _traceParentSpanId;

        // 组件宿主（Express 工具加载）
        private Component.ComponentHost? componentHost;
        private readonly ModuleBus _moduleBus = new();


        /// <summary>由 SpawnCheck 创建，传入初始消息。</summary>
        public ChannelEngine(ISystemContext ctx, SessionContext initialContext, IncomingMessage initialMessage)
        {
            this.ctx = ctx;
            this.channelId = initialContext.Channel.Id;
            this.channelName = initialContext.Channel.Name;
            this.channelConfig = ChannelStateManager.LoadConfig(channelId, initialContext.Channel.Affinity);
            this.impulseTracker = new ImpulseTracker(ctx.ImpulseConfig, channelConfig.Affinity, channelId);
            var now = DateTime.Now;
            this.lastBufferTime = now;
            agentCore.CallerTag = $"Channel:{channelId}";

            _traceParentSpanId = Logging.SignalContext.Current?.CurrentSpanId;

            // ── 上下文服务（DB 派生参与者，无累积状态）──
            _ctxState = new ChannelContextState { ChannelId = channelId, ChannelName = channelName, Mode = "express" };
            _ctxService = new ChannelContextService(new SystemContextDataSource(ctx));

            // 播种参与者数，避免冷启动 Accumulate 用初值 1 导致折扣算错（busy 群削弱话唠抑制）
            try { _lastKnownParticipantCount = Math.Max(1, _ctxService.GetParticipantsAsync(channelId).GetAwaiter().GetResult().Count); }
            catch { /* 查询失败保持初值 1 */ }

            // ── 持久化 + 状态恢复 ──
            persistence = new ChannelContextPersistence(channelId);
            {
                var (_, _, _, savedCursor, _) = persistence.LoadContext();
                _lastConsumedMessageId = savedCursor;
            }

            // ── Agent 配置 ──
            agentConfig = new AgentConfig
            {
                MaxRounds = 20,
                ExpressMaxRounds = 8,
            };

            // ── Gate ──
            gate = new Gate(ctx.EventBus);
            gate.EventFilter = _ => false;

            CollectImagePaths(initialMessage);
            impulseTracker.Accumulate(initialMessage, _lastKnownParticipantCount, initialMessage.IsSystemEvent);
            _lastSessionContext = initialContext;
            _bufferedMessageCount = 1;
            _bufferTriggered = impulseTracker.ShouldRespond(initialMessage, _bufferedMessageCount, null);
            InitModules();
            if (_bufferTriggered)
                ScheduleBufferSignal();

        }

        /// <summary>冷启动：无初始消息，仅恢复频道上下文。供组件唤醒等场景使用。</summary>
        internal ChannelEngine(ISystemContext ctx, Database.Channel channel)
        {
            this.ctx = ctx;
            this.channelId = channel.Id;
            this.channelName = channel.Name;
            this.channelConfig = ChannelStateManager.LoadConfig(channelId, channel.Affinity);
            this.impulseTracker = new ImpulseTracker(ctx.ImpulseConfig, channelConfig.Affinity, channelId);
            this.lastBufferTime = DateTime.Now;
            agentCore.CallerTag = $"Channel:{channelId}";

            // ── 上下文服务（DB 派生参与者，无累积状态）──
            _ctxState = new ChannelContextState { ChannelId = channelId, ChannelName = channelName, Mode = "express" };
            _ctxService = new ChannelContextService(new SystemContextDataSource(ctx));

            // 播种参与者数，避免冷启动 Accumulate 用初值 1 导致折扣算错（busy 群削弱话唠抑制）
            try { _lastKnownParticipantCount = Math.Max(1, _ctxService.GetParticipantsAsync(channelId).GetAwaiter().GetResult().Count); }
            catch { /* 查询失败保持初值 1 */ }

            persistence = new ChannelContextPersistence(channelId);
            {
                var (_, _, _, savedCursor, _) = persistence.LoadContext();
                _lastConsumedMessageId = savedCursor;
            }

            agentConfig = new AgentConfig
            {
                MaxRounds = 20,
                ExpressMaxRounds = 8,
            };

            gate = new Gate(ctx.EventBus);
            gate.EventFilter = _ => false;

            InitModules();
        }

        private void InitModules()
        {
            loopControlModule.ChannelId = channelId.ToString();
            modules = new List<EngineModule>
            {
                loopControlModule,
                delegationNotificationModule
            };
            foreach (var m in modules) m.Attach(bus);

            // 订阅 ToolExecutedEvent — 输出追踪
            bus.Subscribe<ToolExecutedEvent>(e =>
            {
                if (!e.Result.IsSuccess) return;
                var meta = Tool.ToolRegistry.GetMeta(e.Call.Tool);

                if (meta?.OutputOnly == true)
                {
                    hadSpeakThisRound = true;
                }
            });
        }

        /// <summary>由 SpawnCheck 调用，通知新消息到达。不存消息本身（已在 DB），只做冲动值+参与者+触发判断。</summary>
        public void EnqueueMessage(IncomingMessage msg, SessionContext sc, string? traceParentSpanId = null)
        {
            if (msg.IsSelfTriggered) return;

            lock (bufferLock)
            {
                lastBufferTime = DateTime.Now;
                _bufferedMessageCount++;
                CollectImagePaths(msg);
                _traceParentSpanId = traceParentSpanId ?? SignalContext.Current?.CurrentSpanId;

                // @提及+图片 → 立即触发 Phase 2
                if (msg.IsMentioned && msg.Attachments?.Any(a => a.Type == AttachmentType.Image && !string.IsNullOrEmpty(a.Hash)) == true)
                {
                    foreach (var att in msg.Attachments.Where(a => a.Type == AttachmentType.Image && !string.IsNullOrEmpty(a.Hash)))
                    {
                        _pendingPhase2Hashes.Add(att.Hash!);
                        ctx.EventBus.PublishSignal("refine-image",
                            new { hash = att.Hash!, targetPhase = 2, focus = (string?)null, contextText = (string?)null });
                    }
                }
            }
            impulseTracker.Accumulate(msg, _lastKnownParticipantCount, msg.IsSystemEvent);
            loopControlModule.OnNewMessage();
            _lastSessionContext = sc;

            // 禁言状态追踪
            if (msg.SystemEventSubType == "ban") _isBanned = true;
            else if (msg.SystemEventSubType == "unban") _isBanned = false;

            bool isTriggered;
            lock (bufferLock) { isTriggered = _bufferTriggered; }

            // 已触发过 → 只续期计时器
            if (isTriggered)
            {
                ScheduleBufferSignal();
            }
            else if (_isBanned)
            {
                // 禁言期间不唤醒，消息照常 buffer
            }
            else if (ctx.CurrentSleepState != SleepState.None)
            {
                // 睡觉状态：消息 buffer 但不触发思维，除非是 @提及
                if (msg.IsMentioned && !msg.IsSelfTriggered)
                {
                    var isWakeKeyword = SleepUtils.ContainsWakeKeyword(msg.Content);
                    var isLongMessage = SleepUtils.EstimateTokens(msg.Content) >= 15;
                    if (isWakeKeyword || isLongMessage)
                    {
                        // 叫醒 → 走正常流程，第一轮注入起床气
                        _justWokenUp = true;
                        lock (bufferLock) { _bufferTriggered = true; }
                        _triggerHadMention = true;
                        Signal.Event(LogGroup.Engine, "睡眠叫醒",
                            new { channelId, reason = isWakeKeyword ? "关键词" : "长消息吵醒", content = msg.Content[..Math.Min(30, msg.Content.Length)] });
                        ScheduleBufferSignal();
                    }
                    else
                    {
                        // @提及 → 梦话
                        lock (bufferLock) { _bufferTriggered = true; }
                        _triggerHadMention = true;
                        _sleepTalkMode = true;
                        Signal.Event(LogGroup.Engine, "睡眠梦话触发",
                            new { channelId, content = msg.Content[..Math.Min(30, msg.Content.Length)] });
                        ScheduleBufferSignal();
                    }
                }
            }
            else
            {
                // 前置滤波：检查是否该响应
                var shouldRespond = impulseTracker.ShouldRespond(msg, _bufferedMessageCount, LastCompletionTime);
                if (shouldRespond)
                {
                    lock (bufferLock) { _bufferTriggered = true; }
                    _triggerHadMention = msg.IsMentioned && !msg.IsSystemEvent;
                    Signal.Event(LogGroup.Engine, "冲动值决策", new
                    {
                        channelId,
                        decision = "respond",
                        impulse = impulseTracker.Impulse,
                        threshold = ctx.ImpulseConfig.Threshold,
                        messageCount = _bufferedMessageCount,
                        hasMention = msg.IsMentioned,
                        idleSeconds = (int)(DateTime.Now - (LastCompletionTime ?? DateTime.Now)).TotalSeconds
                    });
                    ScheduleBufferSignal();
                }
            }
        }

        /// <summary>唤醒闸门（供外部触发，如 ConsoleProvider 压缩信号）。</summary>
        internal void SignalGate() => gate.Signal();

        /// <summary>DelegationBus 回调：接收到定向消息或广播。</summary>
        private void OnLoopMessageReceived(LoopMessage msg)
        {
            _pendingLoopMessages.Enqueue(msg);
            gate.Signal();
        }

        /// <summary>Drain 跨循环消息队列。</summary>
        internal List<LoopMessage> DrainLoopMessages()
        {
            var list = new List<LoopMessage>();
            while (_pendingLoopMessages.TryDequeue(out var msg))
                list.Add(msg);
            return list;
        }


        public async Task RunAsync()
        {
            // 引擎生命周期信号（Continue 从调用者 signal，自动建立因果连线）
            var parentCtx = Logging.SignalContext.Current;
            var lifeCtx = Signal.Continue(
                SignalContext.NewSignalId(), parentCtx?.CurrentSpanId,
                $"channel:{channelId}", LogGroup.Engine, $"Channel引擎 [{channelName}]",
                new { engineType = EngineType, channelId, channelName });

            // 收集 IInjectProvider：内部模块
            _injectProviders.Clear();
            _injectProviders.AddRange(modules);  // EngineModule : IInjectProvider

            // 注册到委托总线
            var myLoopId = LoopId.ForChannel(channelId);
            _messaging = new Component.AgentMessagingImpl(myLoopId, ctx.DelegationBus,
                () => gate.Signal(), loopId => ctx.DelegationBus.IsLoopActive(loopId),
                () => ctx.DelegationBus.GetActiveLoopIds().ToList());
            delegationNotificationModule.SetMessaging(_messaging);
            _signalFilter = ctx.SignalFilters.GetConfig("channel");
            delegationNotificationModule.SetFilterConfig(_signalFilter);
            ctx.DelegationBus.RegisterLoop(myLoopId, OnLoopMessageReceived);

            // 初始化 ComponentHost（加载 Express 相关 LoopComponent）
            componentHost = new Component.ComponentHost(
                myLoopId, "channel", _moduleBus, ctx.ComponentServices,
                () => gate.ForceWake(),
                new Dictionary<Type, object> { [typeof(IAgentMessaging)] = _messaging });
            componentHost.GlobalHost = ctx.GlobalComponentHost;
            await componentHost.InitAsync();

            SignalContext? sessionCtx = null;

            while (IsAlive)
            {
                // ① 等待闸门（信号到达即放行，冲动值已在 FlushBuffer 侧完成）
                var triggered = await gate.WaitForTriggerAsync(
                    TimeSpan.FromSeconds(ctx.ImpulseConfig.ColdTimeoutSeconds));

                if (!triggered)
                {
                    IsAlive = false;
                    break;
                }

                // 检查频道是否被 SessionEngine Full 占用，若是则跳过本轮
                var sessionEngine = ctx.GetActiveEnginesSnapshot()
                    .OfType<Session.SessionEngine>().FirstOrDefault();
                if (sessionEngine != null && sessionEngine.IsChannelClaimed(channelId))
                {
                    lock (bufferLock)
                    {
                        _bufferedMessageCount = 0;
                        _bufferTriggered = false;
                    }
                    _bufferTimerCts?.Cancel();
                    _bufferTimerCts = null;
                    continue;
                }

                _totalGateCycles++;

                // 排空跨循环消息队列，转为一次性通知（防止同会话每轮重复注入）
                var incomingCross = DrainLoopMessages();
                if (incomingCross.Count > 0 && _messaging is Component.AgentMessagingImpl impl)
                {
                    foreach (var msg in incomingCross)
                        impl.EnqueueIncoming(msg);
                }

                // ② 状态准备：从 DB 检查新消息
                bool hasNewMessages;
                lock (bufferLock)
                {
                    hasNewMessages = _bufferedMessageCount > 0;
                    _bufferedMessageCount = 0;
                    _bufferTriggered = false;
                }
                _bufferTimerCts?.Cancel();
                _bufferTimerCts = null;

                if (hasNewMessages)
                {
                    impulseTracker.ApplyPostResponseUpdate(_triggerHadMention);
                    _triggerHadMention = false;
                }

                if (hasNewMessages)
                {
                    List<(string Path, string? Hash, string? Category)> pendingCopy;
                    lock (bufferLock)
                    {
                        pendingCopy = pendingImageInfos.Count > 0
                            ? new List<(string, string?, string?)>(pendingImageInfos) : new();
                        pendingImageInfos.Clear();
                    }
                    if (pendingCopy.Count > 0)
                        ResolveImagePresentation(pendingCopy);

                    var tickMsgs = await ctx.Session.GetMessagesAfterIdAsync(channelId, _lastConsumedMessageId);
                    foreach (var m in tickMsgs)
                    {
                        processedTicks.AddLast(m.Time.Ticks);
                        while (processedTicks.Count > MaxProcessedTicksWindow)
                            processedTicks.RemoveFirst();

                        if (_ctxService.IsBotMentionedInMessage(m) && !string.IsNullOrEmpty(m.ImageHashes))
                        {
                            foreach (var hash in m.ImageHashes.Split(',', StringSplitOptions.RemoveEmptyEntries))
                            {
                                var trimmed = hash.Trim();
                                var record = await ImageStorage.GetByHashAsync(trimmed);
                                if (record != null && record.Phase == 1)
                                {
                                    var ctxText = BuildSimpleVisionContext();
                                    ctx.EventBus.PublishSignal("refine-image",
                                        new { hash = trimmed, targetPhase = 2, focus = (string?)null, contextText = ctxText });
                                    Signal.Event(LogGroup.Engine, "触发Phase2精炼(@mention)",
                                        new { channelId, hash = trimmed });
                                }
                            }
                        }
                    }
                    processedMessageCount += tickMsgs.Count;

                    // 重置状态
                    fixedPrefix = null;
                }

                // ③ 循环会话
                if (sessionCtx == null)
                {
                    string? parentSpan;
                    lock (bufferLock) { parentSpan = _traceParentSpanId; }

                    if (parentSpan != null)
                        sessionCtx = Signal.Continue(SignalContext.NewSignalId(), parentSpan, $"channel:{channelId}", LogGroup.Engine, "频道会话",
                            new { channelId, mode = "express" });
                    else
                        sessionCtx = Signal.Begin(LogGroup.Engine, $"channel:{channelId}", "频道会话",
                            new { channelId, mode = "express" });
                }

                // ④ 执行
                Interlocked.Exchange(ref _busyFlag, 1);
                try
                {
                    using var roundSpan = Signal.Open(LogGroup.Engine, $"处理轮次 #{_totalGateCycles} [Express]",
                        new { channelId, mode = "express" });

                    if (_sleepTalkMode)
                    {
                        _sleepTalkMode = false;
                        await GenerateAndSendSleepTalkAsync();
                    }
                    else
                    {
                        await ExecuteExpressCycleAsync();
                    }

                    roundSpan.SetCloseDetail(new
                    {
                        mode = "express",
                        hadSpeak = hadSpeakThisRound
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    totalErrorCount++;
                    lastErrorTime = DateTime.Now;
                    lastErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
                    Signal.Error(LogGroup.Engine, "模型调用失败，引擎终止",
                        new { error = ex.GetType().Name, message = ex.Message, channelId });

                    try
                    {
                        var errAdapter = ctx.Adapters.ResolveByChannelId(channelName);
                        if (errAdapter != null)
                        {
                            await errAdapter.SendMessageAsync(new OutgoingMessage
                            {
                                ChannelId = channelName,
                                Content = $"[错误] 处理消息时发生异常：{ex.Message}"
                            });
                        }
                    }
                    catch (Exception notifyEx) { Signal.Warn(LogGroup.Adapter, "错误通知发送失败", new { channelId, error = notifyEx.Message }); }

                    IsAlive = false;
                }
                finally
                {
                    if (sessionCtx != null)
                    {
                        sessionCtx?.Close(new { reason = "循环挂起" });
                        sessionCtx = null;
                        SignalContext.Restore(lifeCtx);
                    }
                    Interlocked.Exchange(ref _busyFlag, 0);
                    Interlocked.Exchange(ref _completionTicks, DateTime.Now.Ticks);
                }
            }

            // 注销委托总线
            ctx.DelegationBus.UnregisterLoop(LoopId.ForChannel(channelId));
            (_messaging as Component.AgentMessagingImpl)?.Detach();

            // 清理模块状态
            foreach (var m in modules) m.Reset();
            hadSpeakThisRound = false;

            lifeCtx.Close(new { engineType = EngineType, channelId, reason = "cold_timeout" });
        }


        public void OnEvent(EngineEvent e)
        {
            if (e is SignalEvent sig && sig.SignalName == "delegation-result")
            {
                try
                {
                    var json = Newtonsoft.Json.Linq.JObject.Parse(sig.Payload?.ToString() ?? "{}");
                    var chId = (int?)json["channelId"];
                    var message = (string?)json["message"];
                    if (chId == channelId && !string.IsNullOrEmpty(message) && _lastSessionContext != null)
                    {
                        var sysMsg = new IncomingMessage
                        {
                            Platform = "System",
                            PlatformUserId = "system",
                            ChannelId = channelId.ToString(),
                            Content = message,
                            IsSystemEvent = true,
                            Time = DateTime.Now
                        };
                        EnqueueMessage(sysMsg, _lastSessionContext);
                    }
                }
                catch (Exception ex)
                {
                    Logging.Signal.Warn(Logging.LogGroup.Engine, $"ChannelEngine 处理委托结果失败", ex);
                }
                gate.Signal();
                return;
            }

            if (e is SignalEvent sig2 && sig2.SignalName == "delegation-completed"
                && sig2.Payload?.ToString() == channelId.ToString())
            {
                gate.Signal();
            }
        }

        public void RequestStop()
        {
            IsAlive = false;
            gate.Signal();
        }

        internal WebUI.Services.WorkerSnapshot GetSnapshot() => new()
        {
            ChannelId = channelId,
            ChannelName = channelName,
            IsAlive = IsAlive,
            IsBusy = IsBusy,
            Impulse = impulseTracker.Impulse,
            Threshold = ctx.ImpulseConfig.Threshold,
            ChannelAffinity = channelConfig.Affinity,
            Importance = channelConfig.Importance,
            ActiveExtractionThreshold = channelConfig.ActiveExtractionThreshold,
            LurkingExtractionThreshold = channelConfig.LurkingExtractionThreshold,
            AutoExtractionEnabled = channelConfig.AutoExtractionEnabled,
            UnrespondedMessageCount = unrespondedMessageCount,
            LastCompletionTime = LastCompletionTime,
            TotalRounds = loopControlModule.TotalRounds,
            SilentRounds = loopControlModule.SilentRounds,
            AuthorizedToolCount = ctx.GlobalComponentHost?.GetVisibleTools("channel").Count() ?? 0,
            ParticipantCount = 0, // 参与者数量现由 ChannelContextService 动态派生，无累积计数
            ProcessedMessageCount = processedMessageCount,
            TotalErrorCount = totalErrorCount,
            LastErrorTime = lastErrorTime,
            LastErrorMessage = lastErrorMessage
        };

        /// <summary>强制唤醒（跳过 ShouldActivate）。</summary>
        public void ForceWake() => gate.Signal();

        // ---- 记忆 ----

        private async Task<string?> BuildMemorySection(SessionContext sc, string query)
        {
            using var memSpan = Signal.Open(LogGroup.Memory, $"记忆检索 p:{sc.Person.Id}",
                new { personId = sc.Person.Id, channelId = sc.Channel.Id, query });
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var task = ctx.MemorySvc.RecallAsync(
                    sc.Person.Id, sc.Channel.Id,
                    query, topK: 5, includeLinks: true, includePersona: true);
                var completed = await Task.WhenAny(task, Task.Delay(10000, cts.Token));

                if (completed != task)
                {
                    memSpan.SetCloseDetail(new { result = "timeout" });
                    return null;
                }
                cts.Cancel();

                var results = await task;
                if (results == null || results.Count == 0)
                {
                    memSpan.SetCloseDetail(new { result = "empty", count = 0 });
                    return null;
                }

                var items = results.Where(m => !m.IsPersona).ToList();
                if (items.Count == 0)
                {
                    memSpan.SetCloseDetail(new { result = "persona_only", totalCount = results.Count });
                    return null;
                }

                var sb = new StringBuilder("[记忆参考]\n");
                foreach (var m in items)
                {
                    if (m.Certainty < 0.5f)
                        sb.AppendLine($"- {m.Content}（不太确定）");
                    else
                        sb.AppendLine($"- {m.Content}");
                }
                memSpan.SetCloseDetail(new
                {
                    result = "ok",
                    count = items.Count,
                    memories = items.Select(m => new { m.Content, m.Certainty, m.Score })
                });
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                memSpan.SetCloseDetail(new { result = "error", error = ex.GetType().Name, message = ex.Message });
                return null;
            }
        }

        private async Task IncrementDailyProgressAsync(Person person)
        {
            var cfg = ctx.TrustConfig;
            var today = DateTime.Today;
            var personId = person.Id;

            if (dailyProgressTracker.TryGetValue(personId, out var entry) && entry.Date == today)
            {
                if (entry.Accumulated >= cfg.DailyInteractionCap) return;
                var newAcc = entry.Accumulated + cfg.DailyInteractionIncrement;
                dailyProgressTracker[personId] = (today, newAcc);
            }
            else
            {
                dailyProgressTracker[personId] = (today, cfg.DailyInteractionIncrement);
            }

            person.TrustProgress += cfg.DailyInteractionIncrement;
            await ctx.Session.UpdatePersonAsync(person);
        }

        // ── Vision 辅助 ──

        private async Task<string?> ResolveImageRefAsync(string imageRef)
        {
            if (string.IsNullOrEmpty(imageRef)) return null;

            // 直接 hash（32 位 hex）
            if (Regex.IsMatch(imageRef, @"^[a-f0-9]{32}$"))
                return imageRef;

            // [IMG:N] 索引：从上轮消息的 ImageHashes 列表中查找
            var match = Regex.Match(imageRef, @"\[IMG:(\d+)\]");
            if (match.Success)
            {
                var idx = int.Parse(match.Groups[1].Value);
                // 获取最近一轮新消息的图片哈希
                var lastNewMsgs = await ctx.Session.GetLatestMessagesAfterIdAsync(channelId, _lastConsumedMessageId, 20);
                var allHashes = new List<string>();
                foreach (var m in lastNewMsgs)
                {
                    if (!string.IsNullOrEmpty(m.ImageHashes))
                        allHashes.AddRange(m.ImageHashes.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(h => h.Trim()));
                }
                if (idx >= 0 && idx < allHashes.Count)
                    return allHashes[idx];
            }

            return null;
        }

        /// <summary>
        /// 为指定图片构建 OCR 文字注入文本。根据文字长度和模式决定注入形式。
        /// 返回 null 表示无需注入（无 OCR 或 HasText 未知）。
        /// </summary>
        private static async Task<string?> BuildOcrInjectionTextAsync(string hash, bool isExpress)
        {
            var record = await ImageStorage.GetByHashAsync(hash);
            if (record?.HasText != true) return null;
            var ocrText = record.OcrText;
            if (string.IsNullOrEmpty(ocrText)) return null;

            var threshold = VisionEngineConfig.Load().OcrRichTextThreshold;
            if (ocrText.Length <= threshold || isExpress)
            {
                return $"[图中文字] {ocrText}";
            }
            else
            {
                var preview = ocrText[..threshold];
                return $"[图中文字] 文字较长({ocrText.Length}字)，预览：{preview}... 使用 get_image_text {hash} 查看全文";
            }
        }

        private string BuildSimpleVisionContext()
        {
            var parts = new List<string>();
            var type = channelName.StartsWith("group") ? "群聊" : "私聊";
            parts.Add($"频道类型：{type}");
            return string.Join("\n", parts);
        }

        private async Task GenerateAndSendSleepTalkAsync()
        {
            try
            {
                var fragments = await CollectSleepTalkFragmentsAsync();
                var talk = await sleepTalkCore.GenerateAsync(fragments);
                if (string.IsNullOrWhiteSpace(talk)) return;
                if (talk.Length > 50) talk = talk[..50];

                var adapter = ctx.Adapters.ResolveByChannelId(channelName);
                if (adapter != null)
                {
                    await adapter.SendMessageAsync(new OutgoingMessage
                    {
                        ChannelId = channelName,
                        Content = talk
                    });
                    hadSpeakThisRound = true;
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "梦话发送失败", new { error = ex.GetType().Name, message = ex.Message });
            }
        }

        private async Task<List<string>> CollectSleepTalkFragmentsAsync()
        {
            var fragments = new List<string>();

            // 当前梦境阶段
            var phase = ctx.CurrentDreamPhase;
            if (!string.IsNullOrEmpty(phase))
                fragments.Add($"梦到了：{phase}");

            // 其他频道的随机消息（排除本频道）
            try
            {
                var allChannels = await ctx.Session.GetAllChannelsAsync();
                var otherChannels = allChannels.Where(c => c.Id != channelId).ToList();
                if (otherChannels.Count > 0)
                {
                    var rng = new Random();
                    for (int i = 0; i < 3; i++)
                    {
                        var randomChannel = otherChannels[rng.Next(otherChannels.Count)];
                        var recent = await ctx.Session.GetContextByChannelAsync(randomChannel.Id, limit: 3);
                        if (recent.Count > 0)
                        {
                            var randomMsg = recent[rng.Next(recent.Count)];
                            if (!string.IsNullOrEmpty(randomMsg.Content))
                            {
                                var snippet = randomMsg.Content.Length > 20
                                    ? randomMsg.Content[..20] + "..."
                                    : randomMsg.Content;
                                fragments.Add(snippet);
                            }
                        }
                    }
                }
            }
            catch { /* 非关键路径 */ }

            return fragments;
        }
    }
}
