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

        /// <summary>频道 ID（公开，供 SetWatchRuleTool 查找）。</summary>
        public int ChannelId => channelId;

        /// <summary>是否正在处理消息。</summary>
        public bool IsBusy => Interlocked.Read(ref _busyFlag) == 1;
        public Component.ComponentHost? ComponentHost => componentHost;

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

        // ---- 冲动值 ----
        private readonly ImpulseTracker impulseTracker;

        // 参与者追踪
        private readonly ConcurrentDictionary<int, ParticipantInfo> recentParticipants = new();

        // Core 实例
        private readonly AgentCore agentCore = new();

        // ── 统一循环（Phase 1）──
        private Gate gate = null!;
        private Agent? agent;
        private AgentConfig agentConfig = null!;
        private ChannelContextPersistence? persistence;
        private CompressionTierModule? compressionTierModule;

        // ── 堆叠式上下文 ──
        private string? fixedPrefix;
        private string? contextSummary;
        private List<Message>? _loadedConversation;
        private int _frameworkMessageCount;

        // 事件总线 + 内务模块
        private readonly LoopBus bus = new();
        private readonly LoopControlModule loopControlModule = new();
        private readonly DelegationNotificationModule delegationNotificationModule = new();
        private List<EngineModule> modules = null!;

        // Component 系统
        private readonly ModuleBus _moduleBus = new();
        private ComponentHost? componentHost;

        // 已处理消息标记
        private readonly LinkedList<long> processedTicks = new();
        private const int MaxProcessedTicksWindow = 50;

        // 跨循环请求队列
        private readonly ConcurrentQueue<CrossRequest> _pendingCrossRequests = new();
        internal IAgentMessaging? _messaging;
        private SignalFilterConfig _signalFilter = new();


        // 记忆提取计数（用于退出时判断是否需要收尾提取）
        private int processedMessageCount = 0;
        private int unrespondedMessageCount = 0;
        private SessionContext? lastContext;

        // Express 对话历史：从数据库拉取最近 N 条消息
        private const int HistoryMaxMessages = 20;

        // 记忆提取 Worker（独立信号 + 独立文件）
        private ChannelExtractionWorker extractionWorker = null!;

        // TrustProgress 每日自动增长跟踪
        private readonly Dictionary<int, (DateTime Date, float Accumulated)> dailyProgressTracker = new();

        // Working 模式图片去重追踪（跨轮累积，同 hash 不重复追加图片）
        private readonly HashSet<string> _seenImageHashes = new();

        // StartInject 已消费的最大消息 Id（防止 BuildRoundInjectAsync 重复拉取）
        private int _startInjectMaxId;

        // Express/Working 自适应切换
        private bool isWorkingMode = false;

        // 本轮触发是否因 @提及（用于 impulse 额外扣减）
        private bool _triggerHadMention;

        // 梦话系统
        private readonly SleepTalkCore sleepTalkCore = new();
        private bool _sleepTalkMode;
        private bool _justWokenUp;

        // 模式配置驱动（Phase 2）：当前模式 ID 和定义
        private string _currentModeId = "express";
        private ModeDefinition? _currentModeDef;

        // 统一游标：两种模式共用的最后消费消息 DB Id
        private int _lastConsumedMessageId;
        // escalate 理由暂存（Express→Working 时注入一次）
        private string? _escalateReason;

        // ── 统一循环 Phase 2：信号缓冲 + 双源注入 ──
        private readonly ConcurrentQueue<ChannelSignal> _signalBuffer = new();
        private readonly List<IInjectProvider> _injectProviders = new();

        // 当前处理批次（供 Agent host 注入使用）
        private Dictionary<int, ParticipantInfo>? currentParticipantSnapshot;
        private SessionContext? _lastSessionContext;
        private bool isInWorkingSession = false;
        private bool hadSpeakThisRound;
        private bool hadWorkThisRound; // 本轮是否执行了非输出工具（speak/send_media/wait/deescalate 以外）
        private int _consecutiveSpeakRounds;
        private SpeakGuard? _speakGuard;

        // 错误追踪
        private DateTime? lastErrorTime = null;
        private string? lastErrorMessage = null;
        private int totalErrorCount = 0;

        // 信号追踪：最近入队消息携带的上游信号
        private string? _traceParentSpanId;
        private string? _sessionRootSpanId; // session root span（提取 cause 指向此处，避免指向内部子 span）

        private readonly HashSet<string> _roundImageHashes = new();
        private readonly HashSet<string> _injectedDescriptions = new();
        private readonly HashSet<string> _injectedOcrTexts = new();

        // Phase 6: 关注规则
        private readonly object watchRulesLock = new();
        private List<WatchRule> watchRules = new();


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

            // ── 持久化 + 状态恢复 ──
            persistence = new ChannelContextPersistence(channelId);
            {
                var (savedSummary, savedMode, _, savedCursor, savedReason) = persistence.LoadContext();
                _lastConsumedMessageId = savedCursor;
                _escalateReason = savedReason;
                if (!string.IsNullOrEmpty(savedMode))
                {
                    var modeDef = ModeConfigLoader.GetMode(savedMode);
                    if (modeDef != null && string.Equals(modeDef.MetaType, "Working", StringComparison.OrdinalIgnoreCase))
                    {
                        isWorkingMode = true;
                        _currentModeId = savedMode;
                    }
                    else if (savedMode == "working")
                    {
                        // 兼容旧持久化格式（savedMode 为字面量 "working"）
                        isWorkingMode = true;
                        _currentModeId = ModeConfigLoader.GetEscalateTarget();
                    }
                    else
                    {
                        _currentModeId = savedMode;
                    }
                }
                if (!string.IsNullOrEmpty(savedSummary) && string.IsNullOrEmpty(contextSummary))
                    contextSummary = savedSummary;
            }

            // ── Agent 配置 ──
            agentConfig = new AgentConfig
            {
                MaxRounds = 20,
                ExpressMaxRounds = 8,
                CompressL1Tokens = 30000,
                CompressL2Tokens = 50000,
                CompressL3Tokens = 70000,
                CompressMinTokens = 5000,
                CompressRetainedMessageCount = 6,
                CompressRetainedMaxTokens = 2000
            };

            // ── Gate ──
            gate = new Gate(ctx.EventBus);
            gate.EventFilter = _ => false;

            extractionWorker = new ChannelExtractionWorker(
                ctx, channelId, channelConfig, recentParticipants,
                () => LastCompletionTime);

            CollectImagePaths(initialMessage);
            recentParticipants.TryAdd(initialContext.User.Id, ParticipantInfo.From(initialContext.User, initialContext.Person, initialMessage));
            impulseTracker.Accumulate(initialMessage, recentParticipants.Count, initialMessage.IsSystemEvent);
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

            persistence = new ChannelContextPersistence(channelId);
            {
                var (savedSummary, savedMode, _, savedCursor, savedReason) = persistence.LoadContext();
                _lastConsumedMessageId = savedCursor;
                _escalateReason = savedReason;
                if (!string.IsNullOrEmpty(savedMode))
                {
                    var modeDef = ModeConfigLoader.GetMode(savedMode);
                    if (modeDef != null && string.Equals(modeDef.MetaType, "Working", StringComparison.OrdinalIgnoreCase))
                    {
                        isWorkingMode = true;
                        _currentModeId = savedMode;
                    }
                    else if (savedMode == "working")
                    {
                        // 兼容旧持久化格式（savedMode 为字面量 "working"）
                        isWorkingMode = true;
                        _currentModeId = ModeConfigLoader.GetEscalateTarget();
                    }
                    else
                    {
                        _currentModeId = savedMode;
                    }
                }
                if (!string.IsNullOrEmpty(savedSummary) && string.IsNullOrEmpty(contextSummary))
                    contextSummary = savedSummary;
            }

            agentConfig = new AgentConfig
            {
                MaxRounds = 20,
                ExpressMaxRounds = 8,
                CompressL1Tokens = 30000,
                CompressL2Tokens = 50000,
                CompressL3Tokens = 70000,
                CompressMinTokens = 5000,
                CompressRetainedMessageCount = 6,
                CompressRetainedMaxTokens = 2000
            };

            gate = new Gate(ctx.EventBus);
            gate.EventFilter = _ => false;

            extractionWorker = new ChannelExtractionWorker(
                ctx, channelId, channelConfig, recentParticipants,
                () => LastCompletionTime);

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
                var data = e.Result.Data ?? "";
                var meta = Tool.ToolRegistry.GetMeta(e.Call.Tool);

                if (meta?.OutputOnly == true)
                {
                    hadSpeakThisRound = true;
                }

                // OutputOnly 工具和核心流控工具（wait/deescalate）不算"工作"
                if (meta?.OutputOnly != true && e.Call.Tool is not "wait" and not "deescalate")
                    hadWorkThisRound = true;
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
            recentParticipants.AddOrUpdate(
                sc.User.Id,
                ParticipantInfo.From(sc.User, sc.Person, msg),
                (_, _) => ParticipantInfo.From(sc.User, sc.Person, msg));
            impulseTracker.Accumulate(msg, recentParticipants.Count, msg.IsSystemEvent);
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

            // Phase 6: 检查关注规则
            CheckWatchRulesAsync(msg, sc);
        }

        /// <summary>唤醒闸门（供外部触发，如 ConsoleProvider 压缩信号）。</summary>
        internal void SignalGate() => gate.Signal();

        /// <summary>DelegationBus 回调：接收到定向委托或广播。</summary>
        private void OnCrossRequestReceived(CrossRequest request)
        {
            _pendingCrossRequests.Enqueue(request);
            gate.Signal();
        }

        /// <summary>Drain 跨循环请求队列。</summary>
        internal List<CrossRequest> DrainCrossRequests()
        {
            var list = new List<CrossRequest>();
            while (_pendingCrossRequests.TryDequeue(out var req))
                list.Add(req);
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

            // 收集 IInjectProvider：内部模块 + 插件实例
            _injectProviders.Clear();
            _injectProviders.AddRange(modules);  // EngineModule : IInjectProvider
            var pluginLoader = ctx.PluginLoader;
            if (pluginLoader != null)
            {
                var engineServices = BuildEngineServiceProvider();
                foreach (var type in pluginLoader.InjectProviderTypes)
                {
                    try
                    {
                        var provider = pluginLoader.InstantiateInjectProvider(type, engineServices);
                        if (provider != null)
                            _injectProviders.Add(provider);
                    }
                    catch (Exception ex) { Signal.Warn(LogGroup.Engine, $"插件实例化失败: {type.Name}", new { type = type.FullName, error = ex.Message }); }
                }
            }

            // 初始化 ComponentHost
            var myLoopId = LoopId.ForChannel(channelId);
            _messaging = new Component.AgentMessagingImpl(myLoopId, ctx.CrossRequests,
                () => gate.Signal(), loopId => ctx.DelegationBus.IsLoopActive(loopId),
                () => ctx.DelegationBus.GetActiveLoopIds().ToList());
            delegationNotificationModule.SetMessaging(_messaging);
            _signalFilter = ctx.SignalFilters.GetConfig("channel");
            delegationNotificationModule.SetFilterConfig(_signalFilter);
            _speakGuard = new SpeakGuard();
            componentHost = new ComponentHost(
                myLoopId, "channel", _moduleBus, ctx.ComponentServices,
                () => gate.Signal(),
                new Dictionary<Type, object>
                {
                    [typeof(IAgentMessaging)] = _messaging,
                    [typeof(ISpeakGuard)] = _speakGuard
                });
            componentHost.GlobalHost = ctx.GlobalComponentHost;
            await componentHost.InitAsync();

            // 注册到委托总线
            ctx.DelegationBus.RegisterLoop(myLoopId, OnCrossRequestReceived);

            SignalContext? sessionCtx = null;

            while (IsAlive)
            {
                // ① 等待闸门（信号到达即放行，冲动值已在 FlushBuffer 侧完成）
                var triggered = await gate.WaitForTriggerAsync(
                    TimeSpan.FromSeconds(ctx.ImpulseConfig.ColdTimeoutSeconds));

                if (!triggered)
                {
                    if (processedMessageCount > 0 && lastContext != null)
                        extractionWorker.Trigger(lastContext, null);
                    IsAlive = false;
                    break;
                }

                _totalGateCycles++;
                await componentHost.OnActivatedAsync();

                // 排空跨循环请求队列，转为一次性通知（防止同会话每轮重复注入）
                var incomingCross = DrainCrossRequests();
                if (incomingCross.Count > 0 && _messaging is Component.AgentMessagingImpl impl)
                {
                    foreach (var req in incomingCross)
                        impl.EnqueueIncoming(req);
                }
                var hasPendingNotifications = (_messaging as Component.AgentMessagingImpl)?.HasPendingNotifications == true;

                // ② 状态准备：从 DB 检查新消息
                bool hasNewMessages;
                lock (bufferLock)
                {
                    hasNewMessages = _bufferedMessageCount > 0;
                    _bufferedMessageCount = 0;
                    _bufferTriggered = false;
                }
                // 取消旧缓冲定时器，防止处理期间旧定时器触发幽灵循环。
                // 处理期间到达的消息会通过 EnqueueMessage 创建新定时器，不受影响。
                _bufferTimerCts?.Cancel();
                _bufferTimerCts = null;

                // 消费触发本轮响应的冲动值，防止处理期间到达的消息看到旧高峰值而误触发
                if (hasNewMessages)
                {
                    impulseTracker.ApplyPostResponseUpdate(_triggerHadMention);
                    _triggerHadMention = false;
                }

                if (hasNewMessages)
                {
                    currentParticipantSnapshot = new Dictionary<int, ParticipantInfo>(recentParticipants);

                    // 消费 pending 图片
                    List<(string Path, string? Hash, string? Category)> pendingCopy;
                    lock (bufferLock)
                    {
                        pendingCopy = pendingImageInfos.Count > 0
                            ? new List<(string, string?, string?)>(pendingImageInfos) : new();
                        pendingImageInfos.Clear();
                    }
                    if (pendingCopy.Count > 0)
                        ResolveImagePresentation(pendingCopy);

                    // 从 DB 拉取新消息用于 processedTicks
                    var tickMsgs = await ctx.Session.GetMessagesAfterIdAsync(channelId, _lastConsumedMessageId);
                    foreach (var m in tickMsgs)
                    {
                        processedTicks.AddLast(m.Time.Ticks);
                        while (processedTicks.Count > MaxProcessedTicksWindow)
                            processedTicks.RemoveFirst();

                        // @提及 + 含图 → 触发 Phase 2 精炼
                        if (IsBotMentionedInMessage(m) && !string.IsNullOrEmpty(m.ImageHashes))
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

                    // 重置 Working 轮次状态
                    isInWorkingSession = false;
                    agent = null;
                    fixedPrefix = null;
                }

                // ③ 循环会话
                if (sessionCtx == null)
                {
                    string? parentSpan;
                    lock (bufferLock) { parentSpan = _traceParentSpanId; }

                    if (parentSpan != null)
                        sessionCtx = Signal.Continue(SignalContext.NewSignalId(), parentSpan, $"channel:{channelId}", LogGroup.Engine, "频道会话",
                            new { channelId, mode = isWorkingMode ? "working" : "express" });
                    else
                        sessionCtx = Signal.Begin(LogGroup.Engine, $"channel:{channelId}", "频道会话",
                            new { channelId, mode = isWorkingMode ? "working" : "express" });
                    _sessionRootSpanId = sessionCtx?.CurrentSpanId;
                }

                // ④ 执行
                hadWorkThisRound = false;
                Interlocked.Exchange(ref _busyFlag, 1);
                try
                {
                    await componentHost.OnBeforeInvokeAsync();

                    using var roundSpan = Signal.Open(LogGroup.Engine, $"处理轮次 #{_totalGateCycles} [{(isWorkingMode ? "Working" : "Express")}]",
                        new { channelId, mode = isWorkingMode ? "working" : "express" });

                    if (_sleepTalkMode)
                    {
                        _sleepTalkMode = false;
                        hadWorkThisRound = true;
                        await GenerateAndSendSleepTalkAsync();
                    }
                    else if (isWorkingMode)
                    {
                        await ExecuteWorkingCycleAsync();
                    }
                    else
                    {
                        await ExecuteExpressCycleAsync();
                    }

                    await componentHost.OnAfterInvokeAsync();

                    roundSpan.SetCloseDetail(new
                    {
                        mode = isWorkingMode ? "working" : "express",
                        isInWorkingSession,
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
                    isInWorkingSession = false;
                }
                finally
                {
                    if (!isInWorkingSession && sessionCtx != null)
                    {
                        sessionCtx?.Close(new { reason = "循环挂起" });
                        sessionCtx = null;
                        SignalContext.Restore(lifeCtx);
                    }
                    if (!isInWorkingSession)
                    {
                        Interlocked.Exchange(ref _busyFlag, 0);
                        Interlocked.Exchange(ref _completionTicks, DateTime.Now.Ticks);
                        await componentHost.OnPauseAsync();
                    }
                }
            }

            // 注销委托总线
            ctx.DelegationBus.UnregisterLoop(LoopId.ForChannel(channelId));
            (_messaging as Component.AgentMessagingImpl)?.Detach();

            // 关闭 ComponentHost
            if (componentHost != null)
                await componentHost.ShutdownAsync(ShutdownReason.Destroy);

            // 清理模块状态
            foreach (var m in modules) m.Reset();
            hadSpeakThisRound = false;
            hadWorkThisRound = false;
            _consecutiveSpeakRounds = 0;
            if (_speakGuard != null) _speakGuard.ConsecutiveSpeakRounds = 0;

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
                    if (chId == channelId && !string.IsNullOrEmpty(message) && lastContext != null)
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
                        EnqueueMessage(sysMsg, lastContext);
                    }
                }
                catch { }
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
            IsWorkingMode = isWorkingMode,
            IsInWorkingSession = isInWorkingSession,
            Impulse = impulseTracker.Impulse,
            Threshold = ctx.ImpulseConfig.Threshold,
            ChannelAffinity = channelConfig.Affinity,
            Importance = channelConfig.Importance,
            ActiveExtractionThreshold = channelConfig.ActiveExtractionThreshold,
            LurkingExtractionThreshold = channelConfig.LurkingExtractionThreshold,
            LastExtractedMessageId = extractionWorker.LastExtractedMessageId,
            LatestMessageId = extractionWorker.LatestMessageId,
            TotalMessageCount = extractionWorker.TotalMessageCount,
            ExtractedMessageCount = extractionWorker.ExtractedMessageCount,
            ExtractionRunning = extractionWorker.IsRunning,
            AutoExtractionEnabled = channelConfig.AutoExtractionEnabled,
            UnrespondedMessageCount = unrespondedMessageCount,
            LastCompletionTime = LastCompletionTime,
            TotalRounds = loopControlModule.TotalRounds,
            SilentRounds = loopControlModule.SilentRounds,
            AuthorizedToolCount = componentHost?.GetAllVisibleToolNames().Count ?? 0,
            ParticipantCount = recentParticipants.Count,
            ProcessedMessageCount = processedMessageCount,
            TotalErrorCount = totalErrorCount,
            LastErrorTime = lastErrorTime,
            LastErrorMessage = lastErrorMessage
        };

        /// <summary>强制唤醒（跳过 ShouldActivate）。</summary>
        public void ForceWake() => gate.Signal();

        /// <summary>强制触发上下文压缩。</summary>
        public void ForceCompress()
        {
            if (agent == null || compressionTierModule == null || compressionTierModule.IsCompressing || IsBusy) return;
            _ = compressionTierModule.CompressAsync(agent.History, (summary, retained) =>
            {
                agent.History.Clear();
                foreach (var m in retained) agent.AddToHistory(m);
                compressionTierModule.SetSummary(summary);
                contextSummary = summary;
            });
        }

        /// <summary>获取上下文快照（供 WebUI 详情页）。</summary>
        internal WebUI.Services.EngineContextSnapshot? GetContextSnapshot()
        {
            if (agent == null) return null;
            var history = agent.History;
            var messages = new List<WebUI.Services.ContextMessageSnapshot>();
            int totalChars = 0;
            foreach (var m in history)
            {
                var est = EstimateCharsLocal(m);
                totalChars += est;
                var snap = new WebUI.Services.ContextMessageSnapshot
                {
                    Role = m.Role,
                    Content = m.Content,
                    EstimatedTokens = est / 3
                };
                if (m.ContentParts != null)
                {
                    snap.Parts = m.ContentParts.Select(p => new WebUI.Services.ContextPartSnapshot
                    {
                        Type = p.Type ?? "text",
                        Text = p.Text?.Truncate(500),
                        ToolName = p.ToolName,
                        ToolInput = p.ToolInput?.Truncate(200),
                        IsError = p.IsError
                    }).ToList();
                }
                messages.Add(snap);
            }
            return new WebUI.Services.EngineContextSnapshot
            {
                EstimatedTokens = totalChars / 3,
                MessageCount = history.Count,
                ConversationOffset = agent.ConversationOffset,
                CompressionTier = compressionTierModule?.CurrentTier ?? CompressionTier.None,
                IsCompressing = compressionTierModule?.IsCompressing ?? false,
                Summary = contextSummary,
                TotalRounds = agent.TotalRounds,
                IsInBackoff = agent.IsInBackoff,
                Messages = messages
            };
        }

        private static int EstimateCharsLocal(Message m)
        {
            if (m.Content != null) return m.Content.Length;
            if (m.ContentParts != null)
                return m.ContentParts.Sum(p =>
                    (p.Text?.Length ?? 0) + (p.ToolInput?.Length ?? 0) + (p.ToolName?.Length ?? 0));
            return 0;
        }

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
                    query, topK: isWorkingMode ? 10 : 5, includeLinks: true, includePersona: true);
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

        private void TrackMemoryExtraction(SessionContext sc)
        {
            this.lastContext = sc;
            extractionWorker.Trigger(sc, _sessionRootSpanId);
        }

        public void TriggerLurkingExtraction()
        {
            if (lastContext == null) return;
            extractionWorker.Trigger(lastContext, null);
        }

        public void ForceExtraction()
        {
            if (lastContext == null) return;
            extractionWorker.ForceTrigger(lastContext, null);
        }

        public void SetAutoExtraction(bool enabled)
        {
            extractionWorker.SetAutoExtraction(enabled);
        }

        public void CancelExtraction()
        {
            extractionWorker.Cancel();
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

        // ---- Phase 6: 关注规则管理 ----

        /// <summary>获取当前关注规则列表（副本）。</summary>
        public List<WatchRule> GetWatchRules()
        {
            lock (watchRulesLock)
            {
                return new List<WatchRule>(watchRules);
            }
        }

        /// <summary>更新关注规则列表。</summary>
        public void UpdateWatchRules(List<WatchRule> rules)
        {
            lock (watchRulesLock)
            {
                watchRules = new List<WatchRule>(rules);
            }
        }

        /// <summary>检查消息是否命中关注规则。</summary>
        private void CheckWatchRulesAsync(IncomingMessage msg, SessionContext sc)
        {
            List<WatchRule> rules;
            lock (watchRulesLock)
            {
                if (watchRules.Count == 0) return;
                rules = new List<WatchRule>(watchRules);
            }

            foreach (var rule in rules)
            {
                bool matched = false;

                // 简单关键词匹配（后续可扩展为正则表达式）
                if (msg.Content.Contains(rule.Pattern, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                }

                if (!matched) continue;

                // 通知系统循环
                var watchSummary = $"规则「{rule.RuleId}」命中：{rule.Description}\n" +
                                   $"消息：{msg.Content.Substring(0, Math.Min(50, msg.Content.Length))}...";
                _messaging!.SubmitFireAndForget(LoopId.System,
                    $"WatchHit: {rule.RuleId}", watchSummary);

                switch (rule.Action)
                {
                    case WatchAction.Notify:
                        break;

                    case WatchAction.Interrupt:
                        if (rule.AutoResponse)
                            gate.Signal();
                        break;

                    case WatchAction.Escalate:
                        _messaging.SubmitFireAndForget(LoopId.System,
                            $"规则「{rule.RuleId}」升级：{rule.Description}", msg.Content);
                        break;
                }
            }
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
