using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    internal class ChannelEngine : ISubEngine, IAgentHost
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

        // ---- 消息缓冲 ----
        private readonly object bufferLock = new();
        private DateTime lastBufferTime;
        private int _bufferedMessageCount; // 自上次 gate 开放以来的新消息计数

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
        private const int ExpressHistoryMaxMessages = 20;

        // 记忆提取 Worker（独立信号 + 独立文件）
        private ChannelExtractionWorker extractionWorker = null!;

        // TrustProgress 每日自动增长跟踪
        private readonly Dictionary<int, (DateTime Date, float Accumulated)> dailyProgressTracker = new();

        // Express/Working 自适应切换
        private bool isWorkingMode = false;

        // 统一游标：两种模式共用的最后消费消息 DB Id
        private int _lastConsumedMessageId;
        private int _pendingCursor;
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

        // 错误追踪
        private DateTime? lastErrorTime = null;
        private string? lastErrorMessage = null;
        private int totalErrorCount = 0;

        // 缓冲定时器
        private CancellationTokenSource? _bufferTimerCts;
        private DateTime? _bufferFirstMessageTime;
        private bool _bufferTriggered;

        // 信号追踪：最近入队消息携带的上游信号
        private string? _traceParentSpanId;
        private string? _sessionRootSpanId; // session root span（提取 cause 指向此处，避免指向内部子 span）

        // 未消费的图片路径
        private readonly List<(string Path, string? Hash, string? Category)> pendingImageInfos = new();

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
                if (savedMode == "working")
                    isWorkingMode = true;
                if (!string.IsNullOrEmpty(savedSummary) && string.IsNullOrEmpty(contextSummary))
                    contextSummary = savedSummary;
            }

            // ── Agent 配置 ──
            agentConfig = new AgentConfig
            {
                MaxRounds = 20,
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
            _bufferTriggered = true;
            InitModules();
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
                if (savedMode == "working")
                    isWorkingMode = true;
                if (!string.IsNullOrEmpty(savedSummary) && string.IsNullOrEmpty(contextSummary))
                    contextSummary = savedSummary;
            }

            agentConfig = new AgentConfig
            {
                MaxRounds = 20,
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
            lock (bufferLock)
            {
                lastBufferTime = DateTime.Now;
                _bufferedMessageCount++;
                CollectImagePaths(msg);
                _traceParentSpanId = traceParentSpanId ?? SignalContext.Current?.CurrentSpanId;
            }
            recentParticipants.AddOrUpdate(
                sc.User.Id,
                ParticipantInfo.From(sc.User, sc.Person, msg),
                (_, _) => ParticipantInfo.From(sc.User, sc.Person, msg));
            impulseTracker.Accumulate(msg, recentParticipants.Count, msg.IsSystemEvent);
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
            else
            {
                // 前置滤波：检查是否该响应
                var shouldRespond = impulseTracker.ShouldRespond(msg, _bufferedMessageCount, LastCompletionTime);
                if (shouldRespond)
                {
                    lock (bufferLock) { _bufferTriggered = true; }
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

        /// <summary>启动/续期缓冲计时器（3s 滑动窗口，10s 上限）。</summary>
        private void ScheduleBufferSignal()
        {
            _bufferTimerCts?.Cancel();
            _bufferTimerCts = new CancellationTokenSource();
            var cts = _bufferTimerCts;

            _bufferFirstMessageTime ??= DateTime.Now;

            var elapsed = (DateTime.Now - _bufferFirstMessageTime.Value).TotalSeconds;
            var remaining = ctx.ImpulseConfig.BufferMaxDelaySeconds - elapsed;
            var delay = Math.Min(ctx.ImpulseConfig.BufferWindowSeconds, Math.Max(remaining, 0.1));

            _ = Task.Delay(TimeSpan.FromSeconds(delay), cts.Token)
                .ContinueWith(_ => FlushBuffer(), TaskContinuationOptions.NotOnCanceled);
        }

        /// <summary>缓冲到期：直接开闸（冲动值已在触发时通过）。</summary>
        private void FlushBuffer()
        {
            _bufferFirstMessageTime = null;
            _bufferTriggered = false;
            gate.Signal();
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
            componentHost = new ComponentHost(
                myLoopId, "channel", _moduleBus, ctx.ComponentServices,
                () => gate.Signal(),
                new Dictionary<Type, object> { [typeof(IAgentMessaging)] = _messaging });
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

                // ② 状态准备：从 DB 检查新消息
                bool hasNewMessages;
                lock (bufferLock)
                {
                    hasNewMessages = _bufferedMessageCount > 0;
                    _bufferedMessageCount = 0;
                    _bufferTriggered = false;
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
                    }
                    processedMessageCount += tickMsgs.Count;

                    // 重置 Working 轮次状态
                    loopControlModule.OnNewMessage();
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

                    if (isWorkingMode)
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

            lifeCtx.Close(new { engineType = EngineType, channelId, reason = "cold_timeout" });
        }


        private static string EscapeXml(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }





        /// <summary>Working 模式：Agent 多轮循环。</summary>
        private async Task ExecuteWorkingCycleAsync()
        {
            EnsureAgent();

            // Lazy register compress tool
            if (compressionTierModule == null)
            {
                compressionTierModule = new CompressionTierModule(agentConfig,
                    () => agent?.History ?? new List<Message>(),
                    () =>
                    {
                        compressionTierModule!.CompressL3Async(
                            agent?.History ?? new List<Message>(),
                            (summary, retained) =>
                            {
                                contextSummary = summary;
                                agent?.ClearHistory();
                                agent?.AddToHistory(new Message { Role = "user", Content = fixedPrefix! });
                                if (!string.IsNullOrEmpty(summary))
                                    agent?.AddToHistory(new Message { Role = "user", Content = $"[上下文摘要]\n{summary}" });
                                // framework = prefix + summary
                                if (agent != null)
                                    agent.ConversationOffset = agent.History.Count;
                                foreach (var m in retained) agent?.AddToHistory(m);
                                PersistCurrentContext();
                            }).GetAwaiter().GetResult();
                    });
                ToolRegistry.Register(new CompressTool(
                    compressionTierModule,
                    agent!.History,
                    (summary, retained) =>
                    {
                        contextSummary = summary;
                        agent.ClearHistory();
                        agent.AddToHistory(new Message { Role = "user", Content = fixedPrefix! });
                        if (!string.IsNullOrEmpty(summary))
                            agent.AddToHistory(new Message { Role = "user", Content = $"[上下文摘要]\n{summary}" });
                        // framework = prefix + summary
                        agent.ConversationOffset = agent.History.Count;
                        foreach (var m in retained) agent.AddToHistory(m);
                        PersistCurrentContext();
                        gate.Signal();
                    }));
            }

            agentCore.AdditionalTools = componentHost!.GetVisibleTools().ToList();
            agentCore.GlobalComponentTools = ctx.GlobalComponentHost?.GetAllTools().ToList();

            // Working 积压检查：游标后有大量未消费消息时，不进 Agent，直接回退 Express
            if (_lastConsumedMessageId > 0)
            {
                var checkMsgs = await ctx.Session.GetMessagesAfterIdAsync(channelId, _lastConsumedMessageId, 31);
                if (checkMsgs.Count > 30)
                {
                    _lastConsumedMessageId = checkMsgs.Max(m => m.Id);
                    Signal.Event(LogGroup.Engine, "自动回退",
                        new { channelId, from = "Working", to = "Express", pendingCount = checkMsgs.Count });
                    isWorkingMode = false;
                    persistence?.SaveContext(null, "express", new List<List<Message>>(),
                        _lastConsumedMessageId, null);
                    EndWorkingSession();
                    gate.Signal();
                    return;
                }
            }

            await agent!.RunAsync(CancellationToken.None);

            if (agent.StopReason == AgentStopReason.Error)
                throw new InvalidOperationException("Agent 连续模型调用失败");

            // 追踪连续输出轮次：无实际工作则累加，有工作则清零
            if (hadWorkThisRound)
                loopControlModule.ConsecutiveOutputOnly = 0;
            else
                loopControlModule.ConsecutiveOutputOnly++;

            // 推进游标（从 BuildStartInjectAsync 的 DB 查询）
            if (_pendingCursor > _lastConsumedMessageId)
                _lastConsumedMessageId = _pendingCursor;

            // Persist after agent finishes
            PersistCurrentContext();

            // Post-processing
            impulseTracker.ApplyPostResponseUpdate();
            if (_lastSessionContext != null)
            {
                TrackMemoryExtraction(_lastSessionContext);
                await IncrementDailyProgressAsync(_lastSessionContext.Person);
            }

            // Handle agent stop reason
            if (agent.StopReason == AgentStopReason.WaitRequested)
            {
                EndWorkingSession();
            }
            else if (agent.StopReason == AgentStopReason.Deescalated)
            {
                var reason = agent.LastRoundCalls?
                    .FirstOrDefault(c => c.Tool == "deescalate")?.Inputs.FirstOrDefault();
                Signal.Event(LogGroup.Engine, "模式切换",
                    new { channelId, from = "Working", to = "Express", reason = reason ?? "工具调用" });
                isWorkingMode = false;

                // 清空 Working 上下文但保留游标
                persistence?.SaveContext(null, "express", new List<List<Message>>(),
                    _lastConsumedMessageId, null);

                EndWorkingSession();

                // 不主动唤醒：让 Express 等待真正的消息到来，避免连续两次空转 Express
            }
            else if (agent.StopReason == AgentStopReason.MaxRounds)
            {
                loopControlModule.AdvanceRound(hadSpeakThisRound);
                if (!loopControlModule.IsMaxRoundsReached)
                    gate.Signal();
                else
                    EndWorkingSession();
            }
            else
            {
                EndWorkingSession();
            }
        }

        /// <summary>Express 模式：单次 Core 调用，不走 Agent。</summary>
        private async Task ExecuteExpressCycleAsync()
        {
            // Build messages for single-shot call
            var messages = new List<Message>();

            var startInject = await ((IAgentHost)this).BuildStartInjectAsync();
            if (startInject != null) messages.AddRange(startInject);

            var roundInject = await ((IAgentHost)this).BuildRoundInjectAsync();
            if (roundInject != null) messages.AddRange(roundInject);

            // Express 总量封顶：新旧消息总共 ExpressHistoryMaxMessages 条，优先保留新消息
            if (messages.Count > ExpressHistoryMaxMessages)
            {
                var excess = messages.Count - ExpressHistoryMaxMessages;
                // 从框架消息之后开始裁剪（保留框架 + 最新消息）
                var maxRemovable = messages.Count - _frameworkMessageCount;
                if (excess > maxRemovable) excess = maxRemovable;
                if (excess > 0)
                    messages.RemoveRange(_frameworkMessageCount, excess);
            }

            // Call model (with retry)
            agentCore.AdditionalTools = componentHost!.GetVisibleTools().ToList();
            agentCore.GlobalComponentTools = ctx.GlobalComponentHost?.GetAllTools().ToList();

            // 跨周期退避：连续 Express 失败时累积延迟
            if (_consecutiveExpressFailures > 0 && _consecutiveExpressFailures <= agentConfig.BackoffSeconds.Length)
            {
                var backoff = agentConfig.BackoffSeconds[_consecutiveExpressFailures - 1];
                await Task.Delay(TimeSpan.FromSeconds(backoff));
            }

            ModelOutput output;
            using (var modelSpan = Signal.Open(LogGroup.Model, $"Express模型调用 ch:{channelId}",
                new
                {
                    mode = "Express", channelId,
                    messageCount = messages.Count,
                    messages = messages.Select(m => m.ContentParts != null
                        ? (object)new { m.Role, parts = m.ContentParts.Select(p => new { p.Type, p.Text, p.ToolName, p.ToolInput, p.ToolUseId, p.IsError }) }
                        : new { m.Role, content = m.Content })
                }))
            {
                output = default!;
                Exception? lastEx = null;
                bool success = false;
                for (int attempt = 0; attempt < agentConfig.ModelCallMaxAttempts; attempt++)
                {
                    try
                    {
                        if (attempt > 0)
                        {
                            var retryDelay = agentConfig.ModelCallRetryDelaySeconds[
                                Math.Min(attempt - 1, agentConfig.ModelCallRetryDelaySeconds.Length - 1)];
                            Signal.Warn(LogGroup.Model, $"Express模型重试 ch:{channelId} #{attempt + 1}",
                                new { channelId, attempt = attempt + 1, delaySeconds = retryDelay, lastError = lastEx?.Message });
                            await Task.Delay(TimeSpan.FromSeconds(retryDelay));
                        }
                        output = await agentCore.InvokeAsync(messages, EngineMode.Express);
                        success = true;
                        _consecutiveExpressFailures = 0;
                        modelSpan.SetCloseDetail(new
                        {
                            responseText = output.Text,
                            thinking = output.Thinking,
                            toolCalls = output.ToolCalls?.Select(tc => new { tc.Tool, tc.Inputs, tc.ToolUseId }),
                            attempts = attempt + 1
                        });
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { lastEx = ex; }
                }
                if (!success)
                {
                    _consecutiveExpressFailures++;
                    modelSpan.SetCloseDetail(new { error = lastEx!.GetType().Name, message = lastEx.Message, attempts = agentConfig.ModelCallMaxAttempts });
                    throw lastEx;
                }
            }

            // Fire-and-forget tools (speak/send_media/escalate etc.)
            if (output.HasToolCalls && output.ToolCalls != null)
            {
                using var expressToolSpan = Signal.Open(LogGroup.Tool, $"Express工具: {string.Join(", ", output.ToolCalls.Select(c => c.Tool))}",
                    new { calls = output.ToolCalls.Select(c => new { c.Tool, c.Inputs }) });
                var executor = new ToolExecutor(componentHost.TryGetTool, null);
                var expressResults = await executor.ExecuteAsync(output.ToolCalls);
                expressToolSpan.SetCloseDetail(new
                {
                    results = output.ToolCalls.Zip(expressResults, (c, r) => new
                    {
                        tool = c.Tool, status = r.Status, data = r.Data, error = r.Error
                    })
                });

                // 发布事件让订阅者处理 speak/send_media 等副作用
                for (int i = 0; i < output.ToolCalls.Count; i++)
                {
                    var call = output.ToolCalls[i];
                    var result = expressResults[i];
                    var toolDef = componentHost.TryGetTool(call.Tool);
                    bus.Publish(new ToolExecutedEvent(call, result, toolDef));
                }

                // Check for escalate
                foreach (var call in output.ToolCalls)
                {
                    if (call.Tool == "escalate")
                    {
                        var reason = call.Inputs.Count > 0 ? call.Inputs[0] : null;
                        _signalBuffer.Enqueue(new ModeSwitchSignal("working", reason));
                        isWorkingMode = true;
                        isInWorkingSession = true;
                        // 直接暂存 reason（BuildStartInjectAsync 在信号清空前就运行）
                        _escalateReason = reason;
                        // 持久化新模式状态（游标保留，后续 Working 从此开始堆叠）
                        persistence?.SaveContext(null, "working", new List<List<Message>>(),
                            _lastConsumedMessageId, reason);
                        Signal.Event(LogGroup.Engine, "模式切换",
                            new { channelId, from = "Express", to = "Working", reason = reason ?? "工具调用" });
                        gate.Signal();
                        break;
                    }
                }
            }
            else if (output.IsText)
            {
                // Express 模式只能通过工具调用发言（speak/send_media 等），
                // 模型直接输出的文本被丢弃，防止绕过工具系统
                Signal.Event(LogGroup.Engine, "Express文本已丢弃",
                    new { channelId, text = output.Text, reason = "模型未使用工具调用直接输出文本" });
            }

            // Post-processing
            impulseTracker.ApplyPostResponseUpdate();
            if (_lastSessionContext != null)
            {
                TrackMemoryExtraction(_lastSessionContext);
                await IncrementDailyProgressAsync(_lastSessionContext.Person);
            }

            // 推进游标
            if (_pendingCursor > _lastConsumedMessageId)
                _lastConsumedMessageId = _pendingCursor;

            // Express 模式持久化游标（防止冷超时重启后消息锚点丢失）
            if (_lastConsumedMessageId > 0)
                persistence?.SaveContext(contextSummary, "express", new List<List<Message>>(),
                    _lastConsumedMessageId, _escalateReason);
        }

        // ═══════════════════════════════════════════════════════════
        // Agent 相关（堆叠式上下文 + 持久化）
        // ═══════════════════════════════════════════════════════════

        private string BuildFixedPrefix()
        {
            var sb = new StringBuilder();

            if (agentCore.UseNativeTools)
            {
                sb.AppendLine("[系统配置]");
                var botId = ctx.Adapters.GetBotPlatformId("qq");
                if (!string.IsNullOrEmpty(botId))
                    sb.AppendLine($"身份信息：你的QQ号是 {botId}。");
                sb.AppendLine(FormatChannelContext());
                sb.AppendLine("[图片说明] 消息末尾附带用户发送的图片。正文中的 [IMG:N] 标记表示图片在原文中的位置，对应末尾第 N 张图片。");
            }
            else
            {
                var workingTools = componentHost!.GetAllVisibleToolNames();
                sb.AppendLine(ToolRegistry.GenerateDescriptions(authorizedTools: workingTools,
                    additionalTools: componentHost!.GetAllVisibleTools().ToList()));
                var botId = ctx.Adapters.GetBotPlatformId("qq");
                if (!string.IsNullOrEmpty(botId))
                    sb.AppendLine($"\n身份信息：你的QQ号是 {botId}。");
                sb.AppendLine(FormatChannelContext());
                sb.AppendLine("\n[图片说明] 消息末尾附带用户发送的图片。正文中的 [IMG:N] 标记表示图片在原文中的位置，对应末尾第 N 张图片。");
            }

            return sb.ToString();
        }

        private string FormatChannelContext()
        {
            if (string.IsNullOrEmpty(channelName)) return "";
            var parts = channelName.Split('_', 2);
            if (parts.Length != 2) return "";
            var type = parts[0];
            var id = parts[1];
            return type switch
            {
                "group" => $"当前频道：群聊，群号: {id}。对群成员操作时需提供 group_id={id}。",
                "private" => $"当前频道：私聊，对方QQ: {id}。",
                _ => ""
            };
        }

        private void EnsureAgent()
        {
            if (agent != null) return;

            fixedPrefix = BuildFixedPrefix();

            var authorized = componentHost!.GetAllVisibleToolNames();
            agent = new Agent(this, agentCore, agentConfig, authorized, componentHost!.TryGetTool);
            agent.OnToolExecuted = (call, result, toolDef) =>
            {
                bus.Publish(new ToolExecutedEvent(call, result, toolDef));
                return Task.CompletedTask;
            };

            // Restore persisted context (loaded into _loadedConversation for BuildStartInjectAsync)
            if (persistence != null && _loadedConversation == null)
            {
                var (summary, mode, rounds, cursor, reason) = persistence.LoadContext();
                if (!string.IsNullOrEmpty(summary) && string.IsNullOrEmpty(contextSummary))
                    contextSummary = summary;
                if (cursor > _lastConsumedMessageId)
                    _lastConsumedMessageId = cursor;
                if (!string.IsNullOrEmpty(reason) && string.IsNullOrEmpty(_escalateReason))
                    _escalateReason = reason;
                if (rounds.Count > 0)
                {
                    _loadedConversation = new List<Message>();
                    foreach (var round in rounds)
                        foreach (var msg in round)
                            if (!IsEmptyMessage(msg))
                                _loadedConversation.Add(msg);
                }
            }
        }

        private IServiceProvider BuildEngineServiceProvider()
        {
            var services = new Dictionary<Type, object>
            {
                [typeof(EventBus)] = ctx.EventBus,
                [typeof(ModuleBus)] = _moduleBus,
                [typeof(Gate)] = gate!,
                [typeof(IAgentMessaging)] = _messaging!,
            };
            return new Component.SimpleServiceProvider(services);
        }

        public int FrameworkMessageCount => _frameworkMessageCount;

        // ═══════════════════════════════════════════════════════════
        // 消息格式化辅助（reply / mention / quoted-context）
        // ═══════════════════════════════════════════════════════════

        private string? _botPlatformId;

        private string? GetBotPlatformId()
        {
            if (_botPlatformId == null)
                _botPlatformId = ctx.Adapters.GetBotPlatformId("qq");
            return _botPlatformId;
        }

        /// <summary>检查 DB 消息中 bot 是否被 @提及。</summary>
        private bool IsBotMentionedInMessage(UserMessage m)
        {
            if (string.IsNullOrEmpty(m.MentionedPlatformIds)) return false;
            var botId = GetBotPlatformId();
            if (string.IsNullOrEmpty(botId)) return false;
            return m.MentionedPlatformIds.Split(',').Any(id => id == botId);
        }

        /// <summary>为 Working XML <message> 构建额外属性（id / reply / mentioned / mentioned_users）。</summary>
        private string FormatMessageAttrs(UserMessage m)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(m.PlatformMessageId))
                parts.Add($"id=\"{EscapeXml(m.PlatformMessageId)}\"");
            if (!string.IsNullOrEmpty(m.ReplyToPlatformMessageId))
                parts.Add($"reply=\"{EscapeXml(m.ReplyToPlatformMessageId)}\"");
            if (IsBotMentionedInMessage(m))
                parts.Add("mentioned=\"true\"");
            if (!string.IsNullOrEmpty(m.MentionedPlatformIds))
                parts.Add($"mentioned_users=\"{EscapeXml(m.MentionedPlatformIds)}\"");
            return parts.Count > 0 ? " " + string.Join(" ", parts) : "";
        }

        /// <summary>为一组消息批次构建缺失引用的 <quoted-context> 块（递归展开）。返回文本 + 收集到的图片路径。</summary>
        private async Task<(string Text, List<string> ImagePaths)?> BuildQuotedContextForBatchAsync(
            List<UserMessage> batch, int channelId, int maxDepth, bool includeSurrounding)
        {
            var visibleIds = new HashSet<string>();
            foreach (var m in batch)
                if (!string.IsNullOrEmpty(m.PlatformMessageId))
                    visibleIds.Add(m.PlatformMessageId);

            var included = new HashSet<string>(visibleIds);
            var sb = new StringBuilder();
            var imagePaths = new List<string>();

            foreach (var m in batch)
            {
                if (!string.IsNullOrEmpty(m.ReplyToPlatformMessageId)
                    && !included.Contains(m.ReplyToPlatformMessageId))
                {
                    await AppendQuotedContextRecursiveAsync(sb, imagePaths, m.ReplyToPlatformMessageId,
                        channelId, maxDepth, included, includeSurrounding);
                }
            }

            return sb.Length > 0 ? (sb.ToString(), imagePaths) : null;
        }

        /// <summary>递归构建单条 quoted-context（含引用链展开）。同时收集图片路径。</summary>
        private async Task AppendQuotedContextRecursiveAsync(
            StringBuilder sb, List<string> imagePaths, string targetId, int channelId,
            int remainingDepth, HashSet<string> included, bool includeSurrounding)
        {
            if (remainingDepth <= 0 || string.IsNullOrEmpty(targetId) || included.Contains(targetId))
                return;

            included.Add(targetId);

            var target = await ctx.Session.GetByPlatformMessageIdAsync(channelId, targetId);
            if (target == null) return;

            // 收集目标消息的图片路径
            await CollectImagePaths(target, imagePaths);

            sb.AppendLine("<quoted-context>");

            if (includeSurrounding)
            {
                var surrounding = await ctx.Session.GetContextAroundAsync(target.Id, channelId, 3);
                foreach (var m in surrounding)
                {
                    var name = m.IsFromBot ? "Lilara" : EscapeXml(m.SenderName);
                    var replyAttr = !string.IsNullOrEmpty(m.ReplyToPlatformMessageId)
                        ? $" reply=\"{EscapeXml(m.ReplyToPlatformMessageId)}\"" : "";
                    var quotedAttr = m.PlatformMessageId == targetId ? " quoted=\"true\"" : "";
                    var idAttr = !string.IsNullOrEmpty(m.PlatformMessageId)
                        ? $" id=\"{EscapeXml(m.PlatformMessageId)}\"" : "";
                    sb.AppendLine($"<msg{idAttr} user=\"{name}\"{quotedAttr}{replyAttr}>");
                    sb.AppendLine(EscapeXml(m.Content));
                    sb.AppendLine("</msg>");
                    // 收集上下文消息的图片
                    await CollectImagePaths(m, imagePaths);
                }
            }
            else
            {
                var name = target.IsFromBot ? "Lilara" : EscapeXml(target.SenderName);
                var replyAttr = !string.IsNullOrEmpty(target.ReplyToPlatformMessageId)
                    ? $" reply=\"{EscapeXml(target.ReplyToPlatformMessageId)}\"" : "";
                sb.AppendLine($"<msg id=\"{EscapeXml(targetId)}\" user=\"{name}\" quoted=\"true\"{replyAttr}>");
                sb.AppendLine(EscapeXml(target.Content));
                sb.AppendLine("</msg>");
            }

            sb.AppendLine("</quoted-context>");

            if (!string.IsNullOrEmpty(target.ReplyToPlatformMessageId))
                await AppendQuotedContextRecursiveAsync(sb, imagePaths, target.ReplyToPlatformMessageId,
                    channelId, remainingDepth - 1, included, includeSurrounding);
        }

        /// <summary>收集一条 DB 消息的图片路径到指定列表。</summary>
        private static async Task CollectImagePaths(UserMessage m, List<string> imagePaths)
        {
            if (string.IsNullOrEmpty(m.ImageHashes)) return;
            foreach (var hash in m.ImageHashes.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = await ImageStorage.GetModelInputPathAsync(hash.Trim());
                if (!string.IsNullOrEmpty(path))
                    imagePaths.Add(path);
            }
        }

        /// <summary>构建 quoted-context Message（含图片 ContentParts）。</summary>
        private async Task AddQuotedContextMessage(List<Message> msgs, string text, List<string> imagePaths)
        {
            var msg = new Message { Role = "user", Content = text };
            if (imagePaths.Count > 0)
            {
                var parts = await BuildContentPartsWithImagePaths(text, imagePaths);
                if (parts.Count > 1) msg.ContentParts = parts;
            }
            msgs.Add(msg);
        }

        /// <summary>将文本 + 一批消息的图片组装为 ContentParts（文本在前，图片全部追尾）。</summary>
        private async Task<List<ContentPart>> BuildContentPartsWithImages(string text, IEnumerable<UserMessage> msgs)
        {
            var parts = new List<ContentPart> { ContentPart.FromText(text) };
            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.ImageHashes)) continue;
                foreach (var hash in m.ImageHashes.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var path = await ImageStorage.GetModelInputPathAsync(hash.Trim());
                    if (!string.IsNullOrEmpty(path))
                        parts.Add(ContentPart.FromImagePath(path));
                }
            }
            return parts;
        }

        /// <summary>将文本 + 一组图片 hash 列表组装为 ContentParts。</summary>
        private Task<List<ContentPart>> BuildContentPartsWithImagePaths(string text, IEnumerable<string> imagePaths)
        {
            var parts = new List<ContentPart> { ContentPart.FromText(text) };
            foreach (var path in imagePaths)
            {
                if (!string.IsNullOrEmpty(path))
                    parts.Add(ContentPart.FromImagePath(path));
            }
            return Task.FromResult(parts);
        }

        /// <summary>组装带图片的 Message：Content 保留文本，ContentParts 追图。</summary>
        private async Task AddMessageWithImages(List<Message> msgs, string text, IEnumerable<UserMessage> sourceMsgs)
        {
            var msg = new Message { Role = "user", Content = text };
            var contentParts = await BuildContentPartsWithImages(text, sourceMsgs);
            if (contentParts.Count > 1) // >1 意味着除了 text 还有图片
                msg.ContentParts = contentParts;
            msgs.Add(msg);
        }

        public async Task<List<Message>?> BuildStartInjectAsync()
        {
            var msgs = new List<Message>();

            // 框架前缀（系统配置、工具描述等）
            if (fixedPrefix == null) fixedPrefix = BuildFixedPrefix();
            msgs.Add(new Message { Role = "user", Content = fixedPrefix });

            // 上下文摘要（压缩后的历史）
            if (!string.IsNullOrEmpty(contextSummary))
                msgs.Add(new Message { Role = "user", Content = $"[上下文摘要]\n{contextSummary}" });

            // 统一从 DB 拉取最近 N 条消息（两种模式共用，X 条为历史+新消息总和）
            // Express：新消息多时自动挤掉旧历史（总量封顶 X）
            // Working：此处拉初始上下文；后续轮次由 BuildRoundInjectAsync 强制拉全量
            {
                var recentMsgs = await ctx.Session.GetContextByChannelAsync(channelId, ExpressHistoryMaxMessages);
                if (recentMsgs.Count > 0)
                {
                    // Express：按游标分离，已消费的为历史
                    // Working：全量注入初始上下文，不切割（BuildRoundInjectAsync 内容不持久化到 _history）
                    var historyMsgs = isWorkingMode
                        ? recentMsgs.ToList()
                        : recentMsgs.Where(m => m.Id <= _lastConsumedMessageId).ToList();
                    if (historyMsgs.Count > 0)
                    {
                        if (isWorkingMode)
                        {
                            // 初始历史引用缺省补块（递归 1 层）—— 放在历史消息前面，让模型先看到被引用的内容
                            var histQc = await BuildQuotedContextForBatchAsync(historyMsgs, channelId, maxDepth: 1, includeSurrounding: false);
                            if (histQc != null)
                                await AddQuotedContextMessage(msgs, histQc.Value.Text, histQc.Value.ImagePaths);

                            var histSb = new StringBuilder("<conversation_history>\n");
                            foreach (var m in historyMsgs)
                            {
                                var name = m.IsFromBot ? "assistant" : EscapeXml(m.SenderName);
                                var attrs = FormatMessageAttrs(m);
                                histSb.AppendLine($"<message role=\"{(m.IsFromBot ? "assistant" : "user")}\" sender=\"{name}\"{attrs}>");
                                histSb.AppendLine(EscapeXml(m.Content));
                                histSb.AppendLine("</message>");
                            }
                            histSb.Append("</conversation_history>");
                            await AddMessageWithImages(msgs, histSb.ToString(), historyMsgs);
                        }
                        else
                        {
                            // 历史引用缺省补块（递归 1 层，带 ±3 上下文）—— 放在对话历史前面
                            var histQc = await BuildQuotedContextForBatchAsync(historyMsgs, channelId, maxDepth: 1, includeSurrounding: true);
                            if (histQc != null)
                                await AddQuotedContextMessage(msgs, histQc.Value.Text, histQc.Value.ImagePaths);

                            var histSb = new StringBuilder("[对话历史]\n");
                            foreach (var m in historyMsgs)
                            {
                                var mentionPrefix = IsBotMentionedInMessage(m) ? "[@你] " : "";
                                var name = m.IsFromBot ? "你" : m.SenderName;
                                var msgId = !string.IsNullOrEmpty(m.PlatformMessageId) ? $"[#{m.PlatformMessageId}]" : "";
                                var replyNote = !string.IsNullOrEmpty(m.ReplyToPlatformMessageId) ? $" (回复 #{m.ReplyToPlatformMessageId})" : "";
                                histSb.AppendLine($"{mentionPrefix}{msgId}{name}: {m.Content}{replyNote}");
                            }
                            await AddMessageWithImages(msgs, histSb.ToString(), historyMsgs);
                        }
                    }

                    // 记录待推进游标（本批次最大消息 Id）
                    var maxId = recentMsgs.Max(m => m.Id);
                    if (maxId > _lastConsumedMessageId)
                        _pendingCursor = maxId;
                }
            }

            // 参与者列表注入（名字、平台ID、内部ID、信任等级、权限、快速记忆）
            if (currentParticipantSnapshot != null && currentParticipantSnapshot.Count > 0)
            {
                if (isWorkingMode)
                {
                    var partSb = new StringBuilder("<participants>\n");
                    foreach (var (_, p) in currentParticipantSnapshot)
                    {
                        var memo = !string.IsNullOrEmpty(p.Memo) ? $" memo=\"{EscapeXml(p.Memo)}\"" : "";
                        var nick = !string.IsNullOrEmpty(p.Nickname) && p.Nickname != p.DisplayName ? $" nickname=\"{EscapeXml(p.Nickname)}\"" : "";
                        var aliases = !string.IsNullOrEmpty(p.Aliases) ? $" aliases=\"{EscapeXml(p.Aliases)}\"" : "";
                        partSb.AppendLine($"<user name=\"{EscapeXml(p.DisplayName)}\"{nick}{aliases} platform_id=\"{EscapeXml(p.PlatformId)}\" person_id=\"{p.PersonId}\" trust=\"{p.TrustLevel}\" permission=\"{p.PermissionLevel}\"{memo} />");
                    }
                    partSb.Append("</participants>");
                    msgs.Add(new Message { Role = "user", Content = partSb.ToString() });
                }
                else
                {
                    var partSb = new StringBuilder("[当前参与者]\n");
                    foreach (var (_, p) in currentParticipantSnapshot)
                    {
                        partSb.Append($"- {p.DisplayName}");
                        if (!string.IsNullOrEmpty(p.Aliases))
                            partSb.Append($" (别称:{p.Aliases})");
                        partSb.Append($" [platform_id:{p.PlatformId}, person_id:{p.PersonId}, trust:{p.TrustLevel}]");
                        if (!string.IsNullOrEmpty(p.Memo))
                            partSb.Append($" — {p.Memo}");
                        partSb.AppendLine();
                    }
                    msgs.Add(new Message { Role = "user", Content = partSb.ToString() });
                }
            }

            // Working 模式：framework 到此为止，后面是对话内容
            _frameworkMessageCount = msgs.Count;

            // Working 模式：escalate 理由注入（仅一次）
            if (isWorkingMode && !string.IsNullOrEmpty(_escalateReason))
            {
                msgs.Add(new Message { Role = "user", Content = $"[模式切换] 已从 Express 切换至 Working 模式。切换原因：{_escalateReason}" });
                _escalateReason = null;
            }

            // Working 模式：插入持久化的旧对话 rounds
            if (isWorkingMode && _loadedConversation != null && _loadedConversation.Count > 0)
            {
                msgs.AddRange(_loadedConversation);
                _loadedConversation = null; // 只注入一次
            }

            // 记忆检索注入（从 DB 新消息拼接 query）
            if (_lastSessionContext != null && _lastConsumedMessageId > 0)
            {
                var queryMsgs = await ctx.Session.GetMessagesAfterIdAsync(channelId, _lastConsumedMessageId);
                if (queryMsgs.Count > 0)
                {
                    var query = string.Join(" ", queryMsgs.Where(m => !m.IsFromBot).Select(m => m.Content));
                    if (!string.IsNullOrEmpty(query))
                    {
                        var memorySection = await BuildMemorySection(_lastSessionContext, query);
                        if (!string.IsNullOrEmpty(memorySection))
                            msgs.Add(new Message { Role = "user", Content = memorySection });
                    }
                }
            }

            // IInjectProvider start injections
            var iCtx = new InjectContext
            {
                Mode = isWorkingMode ? "working" : "express",
                CurrentRound = 0,
                MaxRounds = agentConfig.MaxRounds
            };
            foreach (var p in _injectProviders.OrderBy(p => p.InjectPriority))
            {
                try
                {
                    var s = await p.BuildStartInjectAsync(iCtx);
                    if (!string.IsNullOrEmpty(s))
                        msgs.Add(new Message { Role = "user", Content = s });
                }
                catch (Exception ex) { Signal.Warn(LogGroup.Engine, $"InjectProvider.Start失败: {p.GetType().Name}", new { provider = p.GetType().Name, error = ex.Message }); }
            }

            // Component prompt sections
            BuildComponentInjections(msgs);

            Signal.Event(LogGroup.Engine, "上下文组装完成", new
            {
                channelId,
                mode = isWorkingMode ? "working" : "express",
                totalMessages = msgs.Count,
                prefixLen = fixedPrefix?.Length ?? 0,
                summaryLen = contextSummary?.Length ?? 0,
                newMessageCount = _bufferedMessageCount,
                estimatedTokens = msgs.Sum(m => (m.Content?.Length ?? 0)) / 3
            });

            return msgs.Count > 0 ? msgs : null;
        }

        public async Task<List<Message>?> BuildRoundInjectAsync()
        {
            var msgs = new List<Message>();

            // 每轮注入当前时间
            msgs.Add(new Message { Role = "user", Content = $"[系统] 当前时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss dddd} UTC+8 (Asia/Shanghai)" });

            // Drain signal buffer — format each signal type
            while (_signalBuffer.TryDequeue(out var signal))
            {
                switch (signal)
                {
                    case NewMessageSignal nms:
                    {
                        var nmsName = nms.Session.Person.Name ?? nms.Session.User.PlatformId;
                        var nmsAttrs = new List<string>();
                        if (!string.IsNullOrEmpty(nms.Message.PlatformMessageId))
                            nmsAttrs.Add($"id=\"{EscapeXml(nms.Message.PlatformMessageId)}\"");
                        if (!string.IsNullOrEmpty(nms.Message.ReplyTo))
                            nmsAttrs.Add($"reply=\"{EscapeXml(nms.Message.ReplyTo)}\"");
                        if (nms.Message.IsMentioned)
                            nmsAttrs.Add("mentioned=\"true\"");
                        if (nms.Message.MentionedPlatformIds is { Count: > 0 })
                            nmsAttrs.Add($"mentioned_users=\"{EscapeXml(string.Join(",", nms.Message.MentionedPlatformIds))}\"");
                        var nmsAttrStr = nmsAttrs.Count > 0 ? " " + string.Join(" ", nmsAttrs) : "";
                        var nmsText = $"<new_messages>\n<message role=\"user\" sender=\"{EscapeXml(nmsName)}\"{nmsAttrStr}>\n{EscapeXml(nms.Message.Content)}\n</message>\n</new_messages>";
                        var nmsMsg = new Message { Role = "user", Content = nmsText };
                        // 图片：从 IncomingMessage.Attachments 解析
                        if (nms.Message.Attachments is { Count: > 0 })
                        {
                            var imgPaths = new List<string>();
                            foreach (var att in nms.Message.Attachments.Where(a => a.Type == AttachmentType.Image && !string.IsNullOrEmpty(a.Hash)))
                            {
                                var p = await ImageStorage.GetModelInputPathAsync(att.Hash!);
                                if (!string.IsNullOrEmpty(p)) imgPaths.Add(p);
                            }
                            if (imgPaths.Count > 0)
                            {
                                var parts = await BuildContentPartsWithImagePaths(nmsText, imgPaths);
                                if (parts.Count > 1) nmsMsg.ContentParts = parts;
                            }

                            // 文件附件：追加 URL 和元数据，模型可用 download_file 下载
                            var fileAtts = nms.Message.Attachments
                                .Where(a => a.Type == AttachmentType.File && !string.IsNullOrEmpty(a.SourceUrl))
                                .ToList();
                            if (fileAtts.Count > 0)
                            {
                                var fileLines = new StringBuilder();
                                fileLines.AppendLine();
                                fileLines.AppendLine("[消息附件-文件]");
                                foreach (var fa in fileAtts)
                                {
                                    var sizeStr = fa.FileSize.HasValue
                                        ? fa.FileSize.Value >= 1_000_000
                                            ? $"{(fa.FileSize.Value / 1_000_000.0):F1}MB"
                                            : fa.FileSize.Value >= 1_000
                                                ? $"{(fa.FileSize.Value / 1_000.0):F1}KB"
                                                : $"{fa.FileSize.Value}B"
                                        : "未知大小";
                                    fileLines.AppendLine($"- {fa.FileName ?? "未知文件"} ({sizeStr}) url={fa.SourceUrl}");
                                }
                                nmsMsg.Content += fileLines.ToString();
                            }
                        }
                        // 新消息引用缺省补块（递归 2 层）—— 放在消息前面，让模型先看被引用的内容
                        if (!string.IsNullOrEmpty(nms.Message.ReplyTo))
                        {
                            var target = await ctx.Session.GetByPlatformMessageIdAsync(channelId, nms.Message.ReplyTo);
                            if (target == null)
                            {
                                var qcSb = new StringBuilder();
                                var qcImagePaths = new List<string>();
                                var included = new HashSet<string>();
                                await AppendQuotedContextRecursiveAsync(qcSb, qcImagePaths, nms.Message.ReplyTo,
                                    channelId, remainingDepth: 2, included, includeSurrounding: false);
                                if (qcSb.Length > 0)
                                    await AddQuotedContextMessage(msgs, qcSb.ToString(), qcImagePaths);
                            }
                        }
                        msgs.Add(nmsMsg);
                        break;
                    }
                    case BusEventSignal bes:
                        msgs.Add(new Message { Role = "user", Content = $"[系统事件] {bes.Event.GetType().Name}" });
                        break;
                    case CompressionSignal cs:
                        // Rebuild Agent with new summary + retained history
                        contextSummary = cs.Summary;
                        EnsureAgent();
                        agent!.ClearHistory();
                        if (fixedPrefix == null) fixedPrefix = BuildFixedPrefix();
                        agent.AddToHistory(new Message { Role = "user", Content = fixedPrefix });
                        agent.AddToHistory(new Message { Role = "user", Content = $"[上下文摘要]\n{cs.Summary}" });
                        agent.ConversationOffset = agent.History.Count;
                        foreach (var msg in cs.RetainedHistory)
                            agent.AddToHistory(msg);
                        break;
                    case ModeSwitchSignal mss:
                        isWorkingMode = mss.NewMode == "working";
                        if (!string.IsNullOrEmpty(mss.Reason))
                        {
                            if (mss.NewMode == "working")
                            {
                                // Express→Working：暂存理由供 BuildStartInjectAsync 注入
                                if (string.IsNullOrEmpty(_escalateReason))
                                    _escalateReason = mss.Reason;
                                msgs.Add(new Message { Role = "user", Content = $"[系统] 切换到 Working 模式：{mss.Reason}" });
                            }
                            else
                            {
                                msgs.Add(new Message { Role = "user", Content = $"[系统] 切换到 Express 模式：{mss.Reason}" });
                            }
                        }
                        break;
                }
            }

            // 统一新消息追赶：从 DB 拉取游标之后的全量消息（所有模式生效）
            if (_lastConsumedMessageId > 0)
            {
                var newMsgs = await ctx.Session.GetMessagesAfterIdAsync(channelId, _lastConsumedMessageId);
                if (newMsgs.Count > 0)
                {
                    // Express 模式裁剪：只保留最近一组窗口，避免上下文爆炸
                    var allNewMsgs = newMsgs;
                    if (!isWorkingMode && newMsgs.Count > ExpressHistoryMaxMessages)
                    {
                        newMsgs = newMsgs.Skip(newMsgs.Count - ExpressHistoryMaxMessages).ToList();
                    }

                    if (isWorkingMode)
                    {
                        var sb = new StringBuilder("<new_messages>\n");
                        foreach (var m in newMsgs)
                        {
                            var name = m.IsFromBot ? "assistant" : EscapeXml(m.SenderName);
                            var attrs = FormatMessageAttrs(m);
                            sb.AppendLine($"<message role=\"{(m.IsFromBot ? "assistant" : "user")}\" sender=\"{name}\"{attrs}>");
                            sb.AppendLine(EscapeXml(m.Content));
                            sb.AppendLine("</message>");
                        }
                        sb.Append("</new_messages>");
                        // 新消息引用缺省补块（递归 2 层）—— 放在新消息前面
                        var roundQc = await BuildQuotedContextForBatchAsync(newMsgs, channelId, maxDepth: 2, includeSurrounding: false);
                        if (roundQc != null)
                            await AddQuotedContextMessage(msgs, roundQc.Value.Text, roundQc.Value.ImagePaths);

                        // Working：只追当前批次新消息的图片（老图已在 agent 堆叠历史中）
                        await AddMessageWithImages(msgs, sb.ToString(), newMsgs);
                    }
                    else
                    {
                        var sb = new StringBuilder("<新消息（自上次处理后）>\n");
                        foreach (var m in newMsgs)
                        {
                            var mentionPrefix = IsBotMentionedInMessage(m) ? "[@你] " : "";
                            var name = m.IsFromBot ? "你" : m.SenderName;
                            var msgId = !string.IsNullOrEmpty(m.PlatformMessageId) ? $"[#{m.PlatformMessageId}]" : "";
                            var replyNote = !string.IsNullOrEmpty(m.ReplyToPlatformMessageId) ? $" (回复 #{m.ReplyToPlatformMessageId})" : "";
                            sb.AppendLine($"{mentionPrefix}{msgId}{name}: {m.Content}{replyNote}");
                        }
                        sb.Append("</新消息>");
                        // 新消息引用缺省补块（递归 2 层，带 ±3 上下文）—— 放在新消息前面
                        var roundQc = await BuildQuotedContextForBatchAsync(newMsgs, channelId, maxDepth: 2, includeSurrounding: true);
                        if (roundQc != null)
                            await AddQuotedContextMessage(msgs, roundQc.Value.Text, roundQc.Value.ImagePaths);

                        // Express：直接追加该批次所有图片
                        await AddMessageWithImages(msgs, sb.ToString(), newMsgs);
                    }

                    // 游标推进到所有新消息（包括被裁剪的）
                    var maxNewId = allNewMsgs.Max(m => m.Id);
                    if (maxNewId > _lastConsumedMessageId)
                        _lastConsumedMessageId = maxNewId;
                }
            }

            // IInjectProvider round injections
            var roundCtx = new InjectContext
            {
                Mode = isWorkingMode ? "working" : "express",
                CurrentRound = agent?.TotalRounds ?? 1,
                MaxRounds = agentConfig.MaxRounds,
                EstimatedTokens = agent?.History.Sum(m => (m.Content?.Length ?? 0)) / 3 ?? 0
            };
            foreach (var p in _injectProviders.OrderBy(p => p.InjectPriority))
            {
                try
                {
                    var s = await p.BuildRoundInjectAsync(roundCtx);
                    if (!string.IsNullOrEmpty(s))
                        msgs.Add(new Message { Role = "user", Content = s });
                }
                catch (Exception ex) { Signal.Warn(LogGroup.Engine, $"InjectProvider.Round失败: {p.GetType().Name}", new { provider = p.GetType().Name, error = ex.Message }); }
            }

            // Compression tier hint
            if (compressionTierModule != null && agent != null)
            {
                var estTokens = agent.History.Sum(m => (m.Content?.Length ?? 0)) / 3;
                var text = compressionTierModule.GetInjectText(estTokens);
                if (!string.IsNullOrEmpty(text))
                {
                    msgs.Add(new Message { Role = "user", Content = text });
                    if (compressionTierModule.CurrentTier == CompressionTier.L1)
                        compressionTierModule.MarkL1Injected();
                }
            }

            // 连续多轮无实际工作时，提醒可切换回 Express（Working 模型更强，深思也可能是合理的）
            if (isWorkingMode && loopControlModule.ConsecutiveOutputOnly >= 2)
            {
                msgs.Add(new Message { Role = "user", Content = "你已连续多轮没有执行实际工作（仅发言/等待）。如果工作已完成，可用 deescalate 切换回轻量模式；如果需要继续深思或等待结果则不必。" });
            }

            return msgs.Count > 0 ? msgs : null;
        }

        private void BuildComponentInjections(List<Message> msgs)
        {
            if (componentHost != null)
            {
                var groups = ToolListFormatter.CollectGroups(componentHost, ctx.GlobalComponentHost);
                var overview = ToolListFormatter.BuildToolOverviewSection(groups);
                if (!string.IsNullOrEmpty(overview))
                    msgs.Add(new Message { Role = "user", Content = overview });

                var sections = componentHost.BuildPromptSections();
                foreach (var s in sections)
                    if (!string.IsNullOrEmpty(s))
                        msgs.Add(new Message { Role = "user", Content = s });

                var globalSections = ctx.GlobalComponentHost?.BuildPromptSections(
                    new LoopInfo(channelId.ToString(), "channel")) ?? new();
                foreach (var s in globalSections)
                    if (!string.IsNullOrEmpty(s))
                        msgs.Add(new Message { Role = "user", Content = s });
            }
        }

        private void PersistCurrentContext()
        {
            if (persistence == null || agent == null || agent.History.Count == 0) return;

            // Only persist conversation content (skip framework injections)
            var startIdx = agent.ConversationOffset;
            if (startIdx >= agent.History.Count) return;

            var conversation = agent.History.Skip(startIdx).ToList();

            // 按 assistant 回复分割 rounds：每个 round = 前面的 user 消息 + assistant 回复
            var rounds = new List<List<Message>>();
            var currentRound = new List<Message>();
            foreach (var msg in conversation)
            {
                if (IsEmptyMessage(msg)) continue;
                currentRound.Add(msg);
                if (msg.Role == "assistant")
                {
                    rounds.Add(currentRound);
                    currentRound = new List<Message>();
                }
            }
            // 尾部未闭合的 user 消息也保留
            if (currentRound.Count > 0)
                rounds.Add(currentRound);

            persistence.SaveContext(contextSummary, isWorkingMode ? "working" : "express", rounds,
                _lastConsumedMessageId, _escalateReason);
        }

        private static bool IsEmptyMessage(Message m)
            => string.IsNullOrEmpty(m.Content) && (m.ContentParts == null || m.ContentParts.Count == 0);

        private void EndWorkingSession()
        {
            Signal.Event(LogGroup.Engine, "Working会话结束", new
            {
                channelId,
                totalRounds = agent?.TotalRounds ?? 0,
                hadSpeak = hadSpeakThisRound
            });
            isInWorkingSession = false;
            // 清除 agent 确保下次 Working 会话从干净状态开始（防止 BuildStartInjectAsync 重复注入）
            agent = null;
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
                    if (m.Confidence == "low")
                        sb.AppendLine($"- {m.Content}（不太确定）");
                    else
                        sb.AppendLine($"- {m.Content}");
                }
                memSpan.SetCloseDetail(new
                {
                    result = "ok",
                    count = items.Count,
                    memories = items.Select(m => new { m.Content, m.Confidence, m.Score })
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

        private void CollectImagePaths(IncomingMessage msg)
        {
            if (msg.Attachments == null) return;
            foreach (var a in msg.Attachments)
            {
                if (a.Type == AttachmentType.Image && !string.IsNullOrEmpty(a.LocalPath))
                    pendingImageInfos.Add((a.LocalPath!, a.Hash, a.Category));
            }
        }

        private void ResolveImagePresentation(
            List<(string Path, string? Hash, string? Category)> images)
        {
            foreach (var (path, hash, category) in images)
            {
                if (!string.IsNullOrEmpty(hash))
                    _ = ImageStorage.IncrementSeenCountAsync(hash);
            }
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
    }
}
