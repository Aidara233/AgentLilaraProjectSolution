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

        // ---- 消息缓冲 ----
        private readonly object bufferLock = new();
        private readonly List<(IncomingMessage Message, SessionContext Context)> buffer = new();
        private DateTime lastBufferTime;


        // ---- 冲动值 ----
        private readonly ImpulseTracker impulseTracker;

        // 参与者追踪
        private readonly ConcurrentDictionary<int, ParticipantInfo> recentParticipants = new();

        // Core 实例
        private readonly AgentCore agentCore = new();
        private readonly PreprocessingCore preprocessingCore;

        // ── 统一循环（Phase 1）──
        private Gate gate = null!;
        private Agent? agent;
        private AgentConfig agentConfig = null!;
        private ChannelContextPersistence? persistence;
        private CompressionTierModule? compressionTierModule;

        // ── 堆叠式上下文 ──
        private string? fixedPrefix;
        private string? contextSummary;

        // 事件总线 + 内务模块
        private readonly LoopBus bus = new();
        private readonly LoopControlModule loopControlModule = new();
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


        // 记忆提取计数（用于退出时判断是否需要收尾提取）
        private int processedMessageCount = 0;
        private int unrespondedMessageCount = 0;
        private SessionContext? lastContext;

        // 记忆提取 Worker（独立信号 + 独立文件）
        private ChannelExtractionWorker extractionWorker = null!;

        // TrustProgress 每日自动增长跟踪
        private readonly Dictionary<int, (DateTime Date, float Accumulated)> dailyProgressTracker = new();

        // 授权工具集（会话级）
        private readonly HashSet<string> authorizedTools = new();
        private string currentProfileName = "channel";

        // 消息拦截器（由 MasterEngine 注入）
        private List<AgentLilara.PluginSDK.IMessageInterceptor> interceptors = new();
        private readonly List<string> interceptorInjections = new();

        // Express/Working 自适应切换
        private bool isWorkingMode = false;
        private int consecutiveExternalTriggers = 0;

        // ── 统一循环 Phase 2：信号缓冲 + 双源注入 ──
        private readonly ConcurrentQueue<ChannelSignal> _signalBuffer = new();
        private readonly List<IInjectProvider> _injectProviders = new();

        // 当前处理批次（供 Agent host 注入使用）
        private List<(IncomingMessage Message, SessionContext Context)>? activeBatch;
        private Dictionary<int, ParticipantInfo>? currentParticipantSnapshot;
        private IncomingMessage? currentLastMsg;
        private SessionContext? currentLastSc;
        private bool isInWorkingSession = false;
        private bool hadSpeakThisRound;

        // 错误追踪
        private int consecutiveFailures = 0;
        private DateTime? lastErrorTime = null;
        private string? lastErrorMessage = null;
        private int totalErrorCount = 0;
        private const int ChannelMaxConsecutiveBeforeBackoff = 3;
        private const int ChannelBackoffSeconds = 30;

        // 缓冲定时器
        private CancellationTokenSource? _bufferTimerCts;

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
            this.preprocessingCore = new PreprocessingCore(ctx.Embedding);
            agentCore.CallerTag = $"Channel:{channelId}";

            _traceParentSpanId = Logging.SignalContext.Current?.CurrentSpanId;

            // ── 持久化 ──
            persistence = new ChannelContextPersistence(channelId);

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
            gate.ShouldActivate = CheckShouldActivateAsync;
            gate.ExecuteAsync = ExecuteChannelCycleAsync;

            extractionWorker = new ChannelExtractionWorker(
                ctx, channelId, channelConfig, recentParticipants,
                () => LastCompletionTime);

            buffer.Add((initialMessage, initialContext));
            CollectImagePaths(initialMessage);
            recentParticipants.TryAdd(initialContext.User.Id, ParticipantInfo.From(initialContext.User, initialContext.Person, initialMessage));
            impulseTracker.Accumulate(initialMessage, recentParticipants.Count, initialMessage.IsSystemEvent);
            InitModules();
            ScheduleBufferSignal();

        }

        private void InitModules()
        {
            loopControlModule.ChannelId = channelId.ToString();
            modules = new List<EngineModule>
            {
                loopControlModule
            };
            foreach (var m in modules) m.Attach(bus);

            // 直接订阅 ToolExecutedEvent —— 原 SpeakModule/SignalDispatchModule 内联
            bus.Subscribe<ToolExecutedEvent>(e =>
            {
                if (!e.Result.IsSuccess) return;
                var data = e.Result.Data ?? "";
                switch (e.Call.Tool)
                {
                    case "speak":
                        HandleSpeakToolAsync(data).GetAwaiter().GetResult();
                        hadSpeakThisRound = true;
                        break;
                    case "send_media":
                        HandleSendMediaToolAsync(data).GetAwaiter().GetResult();
                        hadSpeakThisRound = true;
                        break;
                    case "memory":
                        HandleMemoryToolAsync(data).GetAwaiter().GetResult();
                        break;
                    case "dream_permission":
                        ctx.EventBus.PublishSignal("dream-permission", null);
                        break;
                    case "force_sleep":
                        ctx.EventBus.PublishSignal("force-sleep", null);
                        break;
                    case "dream_config":
                        ctx.EventBus.PublishSignal("dream-config", data);
                        break;
                    case "adjust_sleep_score":
                        ctx.EventBus.PublishSignal("sleep-score-offset", data);
                        break;
                    case "trigger_red_alert":
                        ctx.EventBus.PublishSignal("red-alert", null);
                        break;
                    case "mark_review_hint":
                        HandleReviewHintToolAsync(data).GetAwaiter().GetResult();
                        break;
                    case "alert":
                        HandleAlertToolAsync(data).GetAwaiter().GetResult();
                        break;
                }
            });
        }

        /// <summary>由 SpawnCheck 调用，将新消息加入缓冲。</summary>
        public void EnqueueMessage(IncomingMessage msg, SessionContext sc, string? traceParentSpanId = null)
        {
            lock (bufferLock)
            {
                buffer.Add((msg, sc));
                lastBufferTime = DateTime.Now;
                CollectImagePaths(msg);
                _traceParentSpanId = traceParentSpanId ?? SignalContext.Current?.CurrentSpanId;
            }
            recentParticipants.AddOrUpdate(
                sc.User.Id,
                ParticipantInfo.From(sc.User, sc.Person, msg),
                (_, _) => ParticipantInfo.From(sc.User, sc.Person, msg));
            impulseTracker.Accumulate(msg, recentParticipants.Count, msg.IsSystemEvent);
            ScheduleBufferSignal();

            // Phase 6: 检查关注规则
            CheckWatchRulesAsync(msg, sc);
        }

        /// <summary>缓冲窗口到期后 Signal 闸门。每次新消息重置定时器。</summary>
        private void ScheduleBufferSignal()
        {
            _bufferTimerCts?.Cancel();
            _bufferTimerCts = new CancellationTokenSource();
            var cts = _bufferTimerCts;
            _ = Task.Delay(TimeSpan.FromSeconds(ctx.ImpulseConfig.BufferWindowSeconds), cts.Token)
                .ContinueWith(_ => gate.Signal(), TaskContinuationOptions.NotOnCanceled);
        }

        /// <summary>注入消息拦截器列表（由 MasterEngine 在创建引擎后调用）。</summary>
        internal void SetInterceptors(List<AgentLilara.PluginSDK.IMessageInterceptor> list)
        {
            interceptors = list.OrderBy(i => i.Priority).ToList();
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
                () => gate.Signal(), loopId => ctx.DelegationBus.IsLoopActive(loopId));
            componentHost = new ComponentHost(
                myLoopId, "channel", _moduleBus, ctx.ComponentServices,
                () => gate.Signal());
            await componentHost.InitAsync();

            // 注册到委托总线
            ctx.DelegationBus.RegisterLoop(myLoopId, OnCrossRequestReceived);

            SignalContext? sessionCtx = null;

            while (IsAlive)
            {
                // ① WaitGate（保留冷超时机制）
                var triggered = await gate.WaitForTriggerAsync(
                    TimeSpan.FromSeconds(ctx.ImpulseConfig.ColdTimeoutSeconds));

                if (!triggered)
                {
                    if (processedMessageCount > 0 && lastContext != null)
                        extractionWorker.Trigger(lastContext, null);
                    IsAlive = false;
                    break;
                }

                // 循环唤醒：通知组件
                _totalGateCycles++;
                await componentHost.OnActivatedAsync();

                // ② CollectBuffer（在创建 session 之前 — 避免 Timer tick 等空唤醒创建无用 session）
                var batch = CollectBuffer();

                // 没新消息且不在 Working 会话中 → 空唤醒（Timer 心跳等），直接跳过
                if (batch == null && !isInWorkingSession)
                {
                    await componentHost.OnPauseAsync();
                    continue;
                }

                // ③ 循环会话开始（有消息或 Working 会话持续中）
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

                // ④ Gate evaluation（会话内部：决定是否开闸）
                bool prepareResult;
                using (var gateSpan = Signal.Open(LogGroup.Engine, $"闸门评估 #{_totalGateCycles} ({batch?.Count ?? 0}条消息)",
                    new { hasMessages = batch?.Count ?? 0, isWorkingMode }))
                {
                    prepareResult = await EvaluateGateAsync(batch);
                    gateSpan.SetCloseDetail(new { passed = prepareResult });
                }

                if (!prepareResult)
                {
                    sessionCtx?.Close(new { reason = "循环挂起" });
                    sessionCtx = null;
                    SignalContext.Restore(lifeCtx);
                    await componentHost.OnPauseAsync();
                    continue;
                }

                // ⑤ 执行本轮（统一循环：Working→Agent，Express→直接Core调用）
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
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    consecutiveFailures++;
                    totalErrorCount++;
                    lastErrorTime = DateTime.Now;
                    lastErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
                    Signal.Error(LogGroup.Engine, "处理异常",
                        new { error = ex.GetType().Name, message = ex.Message, consecutiveFailures });

                    if (currentLastMsg != null && consecutiveFailures <= 1)
                    {
                        try
                        {
                            await ctx.Adapters.SendMessageAsync(currentLastMsg.Platform, new OutgoingMessage
                            {
                                ChannelId = currentLastMsg.ChannelId,
                                Content = $"[错误] 处理消息时发生异常：{ex.Message}"
                            });
                        }
                        catch (Exception notifyEx) { Signal.Warn(LogGroup.Adapter, "错误通知发送失败", new { channelId, error = notifyEx.Message }); }
                    }

                    if (consecutiveFailures >= ChannelMaxConsecutiveBeforeBackoff)
                    {
                        Signal.Warn(LogGroup.Engine, "连续失败退避",
                            new { channelId, consecutiveFailures, backoffSeconds = ChannelBackoffSeconds });
                        await Task.Delay(TimeSpan.FromSeconds(ChannelBackoffSeconds));
                    }

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

            // 关闭 ComponentHost
            if (componentHost != null)
                await componentHost.ShutdownAsync(ShutdownReason.Destroy);

            // 清理模块状态
            foreach (var m in modules) m.Reset();
            hadSpeakThisRound = false;

            lifeCtx.Close(new { engineType = EngineType, channelId, reason = "cold_timeout" });
        }

        // ── 内联工具事件处理（原 SpeakModule / SignalDispatchModule 回调） ──

        private async Task HandleSpeakToolAsync(string rawText)
        {
            if (currentLastMsg == null || currentLastSc == null || currentParticipantSnapshot == null) return;
            unrespondedMessageCount = 0;
            var (content, replyTo, mentions) = ParseBotOutput(rawText, currentParticipantSnapshot);
            using var speakSpan = Signal.Open(LogGroup.Adapter, "发送消息",
                new { channelId, platform = currentLastMsg.Platform, content, replyTo, mentions });
            var sentId = await ctx.Adapters.SendMessageAsync(currentLastMsg.Platform, new OutgoingMessage
            {
                ChannelId = currentLastMsg.ChannelId,
                Content = content,
                ReplyTo = replyTo,
                Mentions = mentions
            });
            speakSpan.SetCloseDetail(new { messageId = sentId });
            await ctx.Session.SaveBotMessageAsync(currentLastSc.Channel.Id, content, sentId);
        }

        private async Task HandleSendMediaToolAsync(string jsonData)
        {
            if (currentLastMsg == null || currentLastSc == null) return;
            unrespondedMessageCount = 0;
            try
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(jsonData);
                var type = json["type"]?.ToString() ?? "";
                var path = json["path"]?.ToString() ?? "";
                var text = json["text"]?.ToString();

                var attachmentType = type switch
                {
                    "image" or "sticker" => AttachmentType.Image,
                    "voice" => AttachmentType.Audio,
                    "file" => AttachmentType.File,
                    _ => AttachmentType.Image
                };

                var attachments = new List<MessageAttachment>
                {
                    new()
                    {
                        Type = attachmentType,
                        LocalPath = IsLocalPath(path) ? path : null,
                        SourceUrl = IsLocalPath(path) ? null : path,
                        Category = type == "sticker" ? "sticker" : null
                    }
                };

                using var mediaSpan = Signal.Open(LogGroup.Adapter, "发送媒体",
                    new { channelId, platform = currentLastMsg.Platform, type, text, attachmentCount = attachments.Count });
                var sentId = await ctx.Adapters.SendMessageAsync(currentLastMsg.Platform, new OutgoingMessage
                {
                    ChannelId = currentLastMsg.ChannelId,
                    Content = text ?? "",
                    Attachments = attachments
                });
                mediaSpan.SetCloseDetail(new { messageId = sentId });
                var desc = $"[发送{type}]" + (string.IsNullOrEmpty(text) ? "" : $" {text}");
                await ctx.Session.SaveBotMessageAsync(currentLastSc.Channel.Id, desc, sentId);
            }
            catch (Exception ex) { Signal.Error(LogGroup.Adapter, "发送媒体失败", new { channelId, error = ex.GetType().Name, message = ex.Message }); }
        }

        private async Task HandleMemoryToolAsync(string content)
        {
            if (currentLastSc == null) return;
            await ctx.MemorySvc.StoreAsync(content, currentLastSc.Person.Id, currentLastSc.Channel.Id,
                type: MemoryType.Fact);
        }

        private async Task HandleReviewHintToolAsync(string content)
        {
            if (currentLastSc == null) return;
            await ctx.ReviewHints.CreateAsync(content, currentLastSc.Person.Id, currentLastSc.Channel.Id);
        }

        private async Task HandleAlertToolAsync(string reason)
        {
            if (currentLastSc == null) return;
            await HandleAlertAsync(currentLastSc.Person, currentLastSc, reason);
        }

        private static bool IsLocalPath(string path)
        {
            return path.Length > 1 && (path[1] == ':' || path.StartsWith('/') || path.StartsWith("\\\\"));
        }



        private List<(IncomingMessage Message, SessionContext Context)>? CollectBuffer()
        {
            lock (bufferLock)
            {
                if (buffer.Count == 0) return null;
                var batch = new List<(IncomingMessage, SessionContext)>(buffer);
                buffer.Clear();
                return batch;
            }
        }

        /// <summary>闸门评估。复用原 PrepareContextAsync 逻辑，返回是否开闸。</summary>
        private async Task<bool> EvaluateGateAsync(
            List<(IncomingMessage Message, SessionContext Context)>? batch)
        {
            bool hasNewMessages = batch != null && batch.Count > 0;

            if (hasNewMessages)
            {
                activeBatch = batch;
                currentLastMsg = batch![^1].Message;
                currentLastSc = batch[^1].Context;
                currentParticipantSnapshot = new Dictionary<int, ParticipantInfo>(recentParticipants);

                if (ctx.MuteMode)
                {
                    Signal.Event(LogGroup.Engine, "静音跳过", new { channelId, messageCount = batch.Count });
                    TrackMemoryExtraction(batch, currentLastSc);
                    return false;
                }

                // 拦截器链：插件可在此介入（如睡眠行为、维护模式等）
                interceptorInjections.Clear();
                if (interceptors.Count > 0)
                {
                    var interceptCtx = new AgentLilara.PluginSDK.MessageInterceptContext
                    {
                        SleepState = (AgentLilara.PluginSDK.SleepState)(int)ctx.CurrentSleepState,
                        ChannelId = channelId,
                        IsPrivate = currentLastMsg.IsPrivate,
                        HasMention = batch.Any(b => b.Message.IsMentioned),
                        ToolContext = ctx.ToolContext,
                        Messages = batch.Select(b => new AgentLilara.PluginSDK.MessageInfo
                        {
                            Content = b.Message.Content,
                            SenderName = b.Context.Person.Name ?? b.Context.User.PlatformId,
                            PersonId = b.Context.Person.Id,
                            IsMentioned = b.Message.IsMentioned,
                            IsPrivate = b.Message.IsPrivate,
                            PermissionLevel = (int)b.Context.User.PermissionLevel
                        }).ToList()
                    };

                    foreach (var interceptor in interceptors)
                    {
                        var result = await interceptor.OnBeforeProcessAsync(interceptCtx);
                        if (result.Action == AgentLilara.PluginSDK.InterceptAction.Skip)
                        {
                            Signal.Event(LogGroup.Engine, "拦截器跳过", new { channelId, interceptor = interceptor.GetType().Name });
                            TrackMemoryExtraction(batch, currentLastSc);
                            return false;
                        }
                        if (result.Action == AgentLilara.PluginSDK.InterceptAction.Handled)
                        {
                            Signal.Event(LogGroup.Engine, "拦截器处理", new { channelId, interceptor = interceptor.GetType().Name });
                            TrackMemoryExtraction(batch, currentLastSc);
                            return false;
                        }
                        if (result.PromptInjection != null)
                            interceptorInjections.Add(result.PromptInjection);
                    }
                }

                var shouldRespond = impulseTracker.ShouldRespond(batch, LastCompletionTime);
                Signal.Event(LogGroup.Engine, "冲动值决策", new
                {
                    channelId,
                    decision = shouldRespond ? "respond" : "skip",
                    impulse = impulseTracker.Impulse,
                    threshold = ctx.ImpulseConfig.Threshold,
                    messageCount = batch.Count,
                    hasMention = batch.Any(b => b.Message.IsMentioned),
                    idleSeconds = (int)(DateTime.Now - (LastCompletionTime ?? DateTime.Now)).TotalSeconds
                });
                if (!shouldRespond)
                {
                    TrackMemoryExtraction(batch, currentLastSc);
                    return false;
                }

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

                // 标记已处理
                foreach (var (msg, _) in batch)
                {
                    processedTicks.AddLast(msg.Time.Ticks);
                    while (processedTicks.Count > MaxProcessedTicksWindow)
                        processedTicks.RemoveFirst();
                }

                // 分类（仅 Express 模式下判断是否升级）
                if (!isWorkingMode)
                {
                    var lastContent = batch.Select(b => b.Message.Content).LastOrDefault() ?? "";
                    var isTask = await preprocessingCore.IsTaskAsync(lastContent);
                    Signal.Event(LogGroup.Engine, "消息分类", new
                    {
                        channelId,
                        result = isTask ? "task" : "chat",
                        content_preview = lastContent.Length > 200 ? lastContent[..200] : lastContent
                    });
                    if (isTask) { isWorkingMode = true; consecutiveExternalTriggers = 0; }
                }

                // 重置 Working 轮次状态
                loopControlModule.OnNewMessage();
                isInWorkingSession = false;
                agent = null;     // 新消息到达时重置 Agent（确保重新生成 fixedPrefix）
                fixedPrefix = null;

                // 同步频道工具 profile
                currentProfileName = ctx.ToolProfiles.GetProfileForChannel(currentLastMsg.ChannelId);
                var profileTools = ctx.ToolProfiles.GetActiveTools(currentProfileName);
                authorizedTools.Clear();
                foreach (var t in profileTools) authorizedTools.Add(t);
            }
            else if (!isInWorkingSession)
            {
                activeBatch = null;
                return false;
            }
            // else: in working session, no new messages → continue processing

            return true;
        }

        /// <summary>Gate.ShouldActivate 委托。供 Gate 框架调用。</summary>
        private async Task<bool> CheckShouldActivateAsync()
        {
            if (isInWorkingSession) return true;
            var batch = CollectBuffer();
            if (batch == null || batch.Count == 0) return false;
            return await EvaluateGateAsync(batch);
        }

        // ═══════════════════════════════════════════════════════════
        // 统一循环执行（Working → Agent / Express → 直接 Core）
        // ═══════════════════════════════════════════════════════════

        /// <summary>Gate.ExecuteAsync 委托。统一循环入口，按模式分发。</summary>
        private async Task ExecuteChannelCycleAsync(CancellationToken ct)
        {
            Interlocked.Exchange(ref _busyFlag, 1);
            try
            {
                await componentHost!.OnBeforeInvokeAsync();

                if (isWorkingMode)
                {
                    await ExecuteWorkingCycleAsync();
                }
                else
                {
                    await ExecuteExpressCycleAsync();
                }

                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                consecutiveFailures++;
                totalErrorCount++;
                lastErrorTime = DateTime.Now;
                lastErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
                Signal.Error(LogGroup.Engine, "处理异常",
                    new { error = ex.GetType().Name, message = ex.Message, consecutiveFailures });

                if (currentLastMsg != null && consecutiveFailures <= 1)
                {
                    try
                    {
                        await ctx.Adapters.SendMessageAsync(currentLastMsg.Platform, new OutgoingMessage
                        {
                            ChannelId = currentLastMsg.ChannelId,
                            Content = $"[错误] 处理消息时发生异常：{ex.Message}"
                        });
                    }
                    catch (Exception notifyEx) { Signal.Warn(LogGroup.Adapter, "错误通知发送失败", new { channelId, error = notifyEx.Message }); }
                }

                if (consecutiveFailures >= ChannelMaxConsecutiveBeforeBackoff)
                {
                    Signal.Warn(LogGroup.Engine, "连续失败退避",
                        new { channelId, consecutiveFailures, backoffSeconds = ChannelBackoffSeconds });
                    await Task.Delay(TimeSpan.FromSeconds(ChannelBackoffSeconds), ct);
                }

                isInWorkingSession = false;
            }
            finally
            {
                if (!isInWorkingSession)
                {
                    Interlocked.Exchange(ref _busyFlag, 0);
                    Interlocked.Exchange(ref _completionTicks, DateTime.Now.Ticks);
                    await componentHost!.OnPauseAsync();
                }
            }
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
                        compressionTierModule!.CompressSyncAsync(
                            agent?.History ?? new List<Message>(),
                            (summary, retained) =>
                            {
                                contextSummary = summary;
                                agent?.ClearHistory();
                                agent?.AddToHistory(new Message { Role = "user", Content = fixedPrefix! });
                                if (!string.IsNullOrEmpty(summary))
                                    agent?.AddToHistory(new Message { Role = "user", Content = $"[上下文摘要]\n{summary}" });
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
                        foreach (var m in retained) agent.AddToHistory(m);
                        PersistCurrentContext();
                        gate.Signal();
                    }));
            }

            await agent!.RunAsync(CancellationToken.None);

            if (agent.StopReason == AgentStopReason.Error)
                throw new InvalidOperationException("Agent 连续模型调用失败");

            // Persist after agent finishes
            PersistCurrentContext();

            // Post-processing
            impulseTracker.ApplyPostResponseUpdate();
            if (activeBatch != null && currentLastSc != null)
            {
                TrackMemoryExtraction(activeBatch, currentLastSc);
                await IncrementDailyProgressAsync(currentLastSc.Person);
            }

            // Handle agent stop reason
            if (agent.StopReason == AgentStopReason.WaitRequested)
            {
                EndWorkingSession();
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

            // Call model (with retry)
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
                    modelSpan.SetCloseDetail(new { error = lastEx!.GetType().Name, message = lastEx.Message, attempts = agentConfig.ModelCallMaxAttempts });
                    throw lastEx;
                }
            }

            // Process text output
            if (output.IsText && currentLastMsg != null && currentLastSc != null && currentParticipantSnapshot != null)
            {
                var text = output.Text!;
                text = await ProcessPokeMarkers(text, currentLastMsg);
                if (!string.IsNullOrEmpty(text))
                    await SendSegmentsAsync(text, currentLastMsg, currentLastSc, currentParticipantSnapshot);
            }

            // Fire-and-forget tools
            if (output.HasToolCalls && output.ToolCalls != null)
            {
                Tool.Core.ManageComponentsTool.CurrentLoop.Value =
                    new Tool.Core.ManageComponentsTool.LoopContext(currentProfileName, $"channel-{channelId}");
                using var expressToolSpan = Signal.Open(LogGroup.Tool, $"Express工具: {string.Join(", ", output.ToolCalls.Select(c => c.Tool))}",
                    new { calls = output.ToolCalls.Select(c => new { c.Tool, c.Inputs }) });
                var executor = new ToolExecutor(null, null);
                var expressResults = await executor.ExecuteAsync(output.ToolCalls);
                expressToolSpan.SetCloseDetail(new
                {
                    results = output.ToolCalls.Zip(expressResults, (c, r) => new
                    {
                        tool = c.Tool, status = r.Status, data = r.Data, error = r.Error
                    })
                });

                // Check for escalate
                foreach (var call in output.ToolCalls)
                {
                    if (call.Tool == "escalate")
                    {
                        var reason = call.Inputs.Count > 0 ? call.Inputs[0] : null;
                        _signalBuffer.Enqueue(new ModeSwitchSignal("working", reason));
                        isWorkingMode = true;
                        Signal.Event(LogGroup.Engine, "模式切换",
                            new { channelId, from = "Express", to = "Working", reason = reason ?? "工具调用" });
                        gate.Signal();
                        break;
                    }
                }
            }

            // Post-processing
            impulseTracker.ApplyPostResponseUpdate();
            if (activeBatch != null && currentLastSc != null)
            {
                TrackMemoryExtraction(activeBatch, currentLastSc);
                await IncrementDailyProgressAsync(currentLastSc.Person);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // Agent 相关（堆叠式上下文 + 持久化）
        // ═══════════════════════════════════════════════════════════

        private const string WorkingAuthorizedTools =
            "speak,send_media,thinking_notes,memory,pinboard,retain_list,task_management," +
            "mark_review_hint,alert,wait,read_file,write_file,delegate_task,adapter_action," +
            "view_image,get_image_text,compress";

        private string BuildFixedPrefix()
        {
            var sb = new StringBuilder();

            if (agentCore.UseNativeTools)
            {
                sb.AppendLine("[系统配置]");
                var botId = ctx.Adapters.GetBotPlatformId("qq");
                if (!string.IsNullOrEmpty(botId))
                    sb.AppendLine($"身份信息：你的QQ号是 {botId}。");
                sb.AppendLine("[图片标记说明] 上下文中的 <img/> 标记表示用户发送的图片。desc/text 属性为自动生成的摘要，仅供快速参考。涉及具体内容时请使用工具查看原图或获取完整文字。");
            }
            else
            {
                var workingTools = new HashSet<string>(WorkingAuthorizedTools.Split(','));
                sb.AppendLine(ToolRegistry.GenerateDescriptions(authorizedTools: workingTools));
                var botId = ctx.Adapters.GetBotPlatformId("qq");
                if (!string.IsNullOrEmpty(botId))
                    sb.AppendLine($"\n身份信息：你的QQ号是 {botId}。");
                sb.AppendLine("\n[图片标记说明] 上下文中的 <img/> 标记表示用户发送的图片。desc/text 属性为自动生成的摘要，仅供快速参考。涉及具体内容时请使用工具查看原图或获取完整文字。");
            }

            return sb.ToString();
        }

        private void EnsureAgent()
        {
            if (agent != null) return;

            fixedPrefix = BuildFixedPrefix();

            var authorized = new HashSet<string>(WorkingAuthorizedTools.Split(','));
            agent = new Agent(this, agentCore, agentConfig, authorized);
            agent.OnToolExecuted = (call, result, toolDef) =>
            {
                bus.Publish(new ToolExecutedEvent(call, result, toolDef));
                return Task.CompletedTask;
            };

            // Restore persisted history (only conversation rounds, no framework injections)
            if (persistence != null)
            {
                var (summary, mode, rounds) = persistence.LoadContext();
                if (!string.IsNullOrEmpty(summary) && string.IsNullOrEmpty(contextSummary))
                    contextSummary = summary;
                if (rounds.Count > 0)
                {
                    foreach (var round in rounds)
                        foreach (var msg in round)
                            agent.AddToHistory(msg);
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

        public async Task<List<Message>?> BuildStartInjectAsync()
        {
            var msgs = new List<Message>();

            // 框架前缀（系统配置、工具描述等）
            if (fixedPrefix == null) fixedPrefix = BuildFixedPrefix();
            msgs.Add(new Message { Role = "user", Content = fixedPrefix });

            // 上下文摘要（压缩后的历史）
            if (!string.IsNullOrEmpty(contextSummary))
                msgs.Add(new Message { Role = "user", Content = $"[上下文摘要]\n{contextSummary}" });

            // Format new messages from buffer (interceptor flow)
            if (currentLastMsg != null && activeBatch != null && activeBatch.Count > 0)
            {
                var sb = new StringBuilder("<新消息>\n");
                foreach (var (msg, sc) in activeBatch)
                {
                    var name = sc.Person.Name ?? sc.User.PlatformId;
                    sb.AppendLine($"{name}: {msg.Content}");
                }
                sb.Append("</新消息>");

                // Interceptor injections
                if (interceptorInjections.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("[系统提示]");
                    foreach (var inj in interceptorInjections)
                        sb.AppendLine(inj);
                }

                msgs.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            // 记忆检索注入
            if (currentLastSc != null && activeBatch != null && activeBatch.Count > 0)
            {
                var query = string.Join(" ", activeBatch.Select(b => b.Message.Content));
                var memorySection = await BuildMemorySection(currentLastSc, query);
                if (!string.IsNullOrEmpty(memorySection))
                    msgs.Add(new Message { Role = "user", Content = memorySection });
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
                newMessageCount = activeBatch?.Count ?? 0,
                estimatedTokens = msgs.Sum(m => (m.Content?.Length ?? 0)) / 3
            });

            return msgs.Count > 0 ? msgs : null;
        }

        public async Task<List<Message>?> BuildRoundInjectAsync()
        {
            var msgs = new List<Message>();

            // Drain signal buffer — format each signal type
            while (_signalBuffer.TryDequeue(out var signal))
            {
                switch (signal)
                {
                    case NewMessageSignal nms:
                        var sb2 = new StringBuilder("<新消息>\n");
                        sb2.AppendLine($"{nms.Session.Person.Name ?? nms.Session.User.PlatformId}: {nms.Message.Content}");
                        sb2.Append("</新消息>");
                        msgs.Add(new Message { Role = "user", Content = sb2.ToString() });
                        break;
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
                        foreach (var msg in cs.RetainedHistory)
                            agent.AddToHistory(msg);
                        break;
                    case ModeSwitchSignal mss:
                        isWorkingMode = mss.NewMode == "working";
                        break;
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

            return msgs.Count > 0 ? msgs : null;
        }

        private void BuildComponentInjections(List<Message> msgs)
        {
            if (componentHost != null)
            {
                var groups = ToolListFormatter.CollectGroups(componentHost, ctx.GlobalComponentHost);
                var overview = ToolListFormatter.BuildToolOverviewSection(groups);
                if (overview != null)
                    msgs.Add(new Message { Role = "user", Content = overview });

                var sections = componentHost.BuildPromptSections();
                foreach (var s in sections)
                    msgs.Add(new Message { Role = "user", Content = s });

                var globalSections = ctx.GlobalComponentHost?.BuildPromptSections(
                    new LoopInfo(channelId.ToString(), "channel")) ?? new();
                foreach (var s in globalSections)
                    msgs.Add(new Message { Role = "user", Content = s });
            }
        }

        private void PersistCurrentContext()
        {
            if (persistence == null || agent == null || agent.History.Count == 0) return;

            // Only persist conversation content (skip framework injections)
            var startIdx = agent.ConversationOffset;
            if (startIdx >= agent.History.Count) return;

            var rounds = new List<List<Message>>();
            var conversation = agent.History.Skip(startIdx).ToList();
            for (int i = 0; i < conversation.Count; i += 2)
            {
                var msg = conversation[i];
                // Skip empty messages
                if (string.IsNullOrEmpty(msg.Content)) continue;
                var pair = new List<Message> { msg };
                if (i + 1 < conversation.Count)
                {
                    var asst = conversation[i + 1];
                    if (!string.IsNullOrEmpty(asst.Content))
                        pair.Add(asst);
                }
                if (pair.Count > 0)
                    rounds.Add(pair);
            }
            persistence.SaveContext(contextSummary, isWorkingMode ? "working" : "express", rounds);
        }

        private void EndWorkingSession()
        {
            Signal.Event(LogGroup.Engine, "Working会话结束", new
            {
                channelId,
                totalRounds = agent?.TotalRounds ?? 0,
                hadSpeak = hadSpeakThisRound
            });
            isInWorkingSession = false;
        }
        public void OnEvent(EngineEvent e)
        {
            if (e is SignalEvent sig && sig.SignalName == "delegation-completed"
                && sig.Payload?.ToString() == channelId.ToString())
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
            ConsecutiveExternalTriggers = consecutiveExternalTriggers,
            LastCompletionTime = LastCompletionTime,
            TotalRounds = loopControlModule.TotalRounds,
            SilentRounds = loopControlModule.SilentRounds,
            AuthorizedToolCount = authorizedTools.Count,
            ParticipantCount = recentParticipants.Count,
            ProcessedMessageCount = processedMessageCount,
            ConsecutiveFailures = consecutiveFailures,
            TotalErrorCount = totalErrorCount,
            LastErrorTime = lastErrorTime,
            LastErrorMessage = lastErrorMessage
        };

        /// <summary>强制唤醒（跳过 ShouldActivate）。</summary>
        public void ForceWake() => gate.ForceWake();

        /// <summary>强制触发上下文压缩。</summary>
        public void ForceCompress()
        {
            if (agent == null || compressionTierModule == null || compressionTierModule.IsCompressing) return;
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

        private void TrackMemoryExtraction(
            List<(IncomingMessage Message, SessionContext Context)> messages, SessionContext sc)
        {
            this.lastContext = sc;
            processedMessageCount += messages.Count;
            unrespondedMessageCount += messages.Count;

            extractionWorker.Trigger(sc, _sessionRootSpanId);
        }

        public void TriggerLurkingExtraction()
        {
            if (lastContext == null) return;
            extractionWorker.Trigger(lastContext, null);
        }

        public void SetAutoExtraction(bool enabled)
        {
            extractionWorker.SetAutoExtraction(enabled);
        }

        public void CancelExtraction()
        {
            extractionWorker.Cancel();
        }


        // ---- Express 轻量动作 ----

        private static readonly Regex PokeRegex = new(@"\[POKE:(\d+)\]", RegexOptions.Compiled);

        private async Task<string> ProcessPokeMarkers(string text, IncomingMessage lastMsg)
        {
            var matches = PokeRegex.Matches(text);
            if (matches.Count == 0) return text;

            foreach (Match match in matches)
            {
                var targetUid = match.Groups[1].Value;
                long? groupId = lastMsg.ChannelId.StartsWith("group_")
                    ? long.Parse(lastMsg.ChannelId[6..])
                    : null;

                var parameters = new Dictionary<string, string> { ["user_id"] = targetUid };
                if (groupId.HasValue) parameters["group_id"] = groupId.Value.ToString();

                var result = await ctx.Adapters.ExecuteActionAsync(lastMsg.Platform, lastMsg.ChannelId, "poke", parameters);
            }

            return PokeRegex.Replace(text, "").Trim();
        }

        // ---- 消息分条发送 ----

        private async Task SendSegmentsAsync(string text, IncomingMessage lastMsg,
            SessionContext lastSc, Dictionary<int, ParticipantInfo> participantSnapshot)
        {
            var segments = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();

            string? firstReplyTo = null;
            var rng = new Random();
            for (int i = 0; i < segments.Count; i++)
            {
                if (i > 0)
                    await Task.Delay(rng.Next(600, 2000));
                var (content, replyTo, mentions) = ParseBotOutput(segments[i], participantSnapshot);
                if (i == 0) firstReplyTo = replyTo;
                using var segSpan = Signal.Open(LogGroup.Adapter, "发送消息段",
                    new { channelId = lastMsg.ChannelId, platform = lastMsg.Platform, segment = i + 1, total = segments.Count, content });
                var sentId = await ctx.Adapters.SendMessageAsync(lastMsg.Platform, new OutgoingMessage
                {
                    ChannelId = lastMsg.ChannelId,
                    Content = content,
                    ReplyTo = i == 0 ? firstReplyTo : null,
                    Mentions = mentions
                });
                segSpan.SetCloseDetail(new { messageId = sentId });
                await ctx.Session.SaveBotMessageAsync(lastSc.Channel.Id, content, sentId);
            }
        }

        private static readonly System.Text.RegularExpressions.Regex AtTagRegex =
            new(@"<at\s+user=""([^""]+)""\s*/>", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex ReplyTagRegex =
            new(@"<reply\s+id=""([^""]+)""\s*/>", System.Text.RegularExpressions.RegexOptions.Compiled);

        private (string Content, string? ReplyTo, List<string>? Mentions) ParseBotOutput(
            string raw, Dictionary<int, ParticipantInfo> participants)
            => BotOutputParser.Parse(raw, participants);


        private async Task HandleAlertAsync(Person person, SessionContext sc, string reason)
            => await AlertHandler.HandleAsync(person, sc, reason, ctx);

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
