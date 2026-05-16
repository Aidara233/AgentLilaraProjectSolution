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
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 频道引擎。长生命周期，一个活跃频道一个实例。
    /// 负责消息缓冲聚合、冲动值决策、参与者追踪、消息处理（分类→记忆→回复→提取）。
    /// </summary>
    internal class ChannelEngine : ISubEngine
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
        private readonly PromptBuilder promptBuilder = new();
        private ContextBuilder contextBuilder = null!;

        // 闸门 + 事件总线 + 内务模块
        private readonly LoopGate gate = new();
        private readonly LoopBus bus = new();
        private readonly SpeakModule speakModule = new();
        private readonly TaskListModule taskListModule = new();
        private readonly MemoryWindowModule memoryWindowModule = new();
        private readonly LoopControlModule loopControlModule = new();
        private readonly SignalDispatchModule signalDispatchModule = new();
        private List<EngineModule> modules = null!;

        // Component 系统
        private ComponentHost? componentHost;

        // 已处理消息标记
        private readonly LinkedList<long> processedTicks = new();
        private const int MaxProcessedTicksWindow = 50;

        // 系统通知队列（系统循环注入，频道循环消费）
        private readonly ConcurrentQueue<string> systemNotifications = new();

        // 记忆缓存：per-person
        private readonly Dictionary<int, (List<ScoredMemory> Results, DateTime Time)> memoryCache = new();
        private const float MemoryCacheTtlSeconds = 60f;

        // 记忆检索意图缓存（同一轮对话内不重复调用 MemoryQueryCore）
        private MemoryQueryIntent? cachedQueryIntent;
        private DateTime cachedQueryIntentTime = DateTime.MinValue;
        private const float QueryIntentCacheTtlSeconds = 30f;

        // 记忆提取计数（用于退出时判断是否需要收尾提取）
        private int processedMessageCount = 0;
        private int unrespondedMessageCount = 0;
        private int lastExtractedMessageId = -1; // -1 表示未初始化，需从 DB 加载
        private int latestMessageId = 0;
        private int totalMessageCount = 0;
        private int extractedMessageCount = 0;
        private bool extractionRunning = false;
        private CancellationTokenSource? extractionCts;
        private SessionContext? lastContext;

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

        // 错误追踪
        private int consecutiveFailures = 0;
        private DateTime? lastErrorTime = null;
        private string? lastErrorMessage = null;
        private int totalErrorCount = 0;
        private const int ChannelMaxConsecutiveBeforeBackoff = 3;
        private const int ChannelBackoffSeconds = 30;

        // Working 会话状态（跨闸门轮次保持）
        private string? currentContextXml;
        private List<ImageEmbed>? currentImageEmbeds;
        private Dictionary<int, ParticipantInfo>? currentParticipantSnapshot;
        private IncomingMessage? currentLastMsg;
        private SessionContext? currentLastSc;
        private List<ToolCall>? lastRoundCalls;
        private List<ToolResult>? lastRoundResults;
        private bool isInWorkingSession = false;
        private string? escalationReason;

        // 缓冲定时器
        private CancellationTokenSource? _bufferTimerCts;

        // 信号追踪：最近入队消息携带的上游信号
        private string? _traceSignalId;
        private string? _traceParentSpanId;

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
            this.contextBuilder = new ContextBuilder(ctx.Session, initialContext.Channel.Id);
            agentCore.CallerTag = $"Channel:{channelId}";

            _traceSignalId = Logging.SignalContext.Current?.SignalId;
            _traceParentSpanId = Logging.SignalContext.Current?.CurrentSpanId;

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
                speakModule, taskListModule,
                memoryWindowModule, loopControlModule, signalDispatchModule,
                new ToolStatusModule(),
                new Modules.SystemNotificationModule(DrainSystemNotifications)
            };
            foreach (var m in modules) m.Attach(bus);
        }

        /// <summary>由 SpawnCheck 调用，将新消息加入缓冲。</summary>
        public void EnqueueMessage(IncomingMessage msg, SessionContext sc, string? traceSignalId = null, string? traceParentSpanId = null)
        {
            lock (bufferLock)
            {
                buffer.Add((msg, sc));
                lastBufferTime = DateTime.Now;
                CollectImagePaths(msg);
                _traceSignalId = traceSignalId ?? SignalContext.Current?.SignalId;
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

        /// <summary>系统循环注入通知到频道循环。唤醒闸门，下一轮 prompt 中展示。</summary>
        public void InjectNotification(string content)
        {
            systemNotifications.Enqueue(content);
            gate.Signal();
        }

        /// <summary>Drain 系统通知队列。</summary>
        internal List<string> DrainSystemNotifications()
        {
            var list = new List<string>();
            while (systemNotifications.TryDequeue(out var n))
                list.Add(n);
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

            WireModuleCallbacks();

            // 初始化 ComponentHost
            componentHost = new ComponentHost(
                channelId.ToString(), "channel", ctx.ComponentEventBus, ctx.ComponentServices,
                () => gate.Signal());
            await componentHost.InitAsync();

            SignalContext? sessionCtx = null;

            while (IsAlive)
            {
                // ① WaitGate
                var triggered = await gate.WaitAsync(
                    TimeSpan.FromSeconds(ctx.ImpulseConfig.ColdTimeoutSeconds));

                if (!triggered)
                {
                    if (processedMessageCount > 0 && lastContext != null)
                        await ExtractMemoryAsync(lastContext);
                    IsAlive = false;
                    break;
                }

                // 循环唤醒：通知组件
                await componentHost.OnActivatedAsync();

                // 收集 trace 信息（上游适配器信号的因果链接）
                string? sigId, parentSpan;
                lock (bufferLock) { sigId = _traceSignalId; parentSpan = _traceParentSpanId; }

                // ② CollectBuffer
                var batch = CollectBuffer();

                // ③ 循环会话开始（如未在会话中则创建；闸门评估在会话内）
                if (sessionCtx == null)
                {
                    if (sigId != null)
                        sessionCtx = Signal.Continue(sigId, parentSpan, $"channel:{channelId}", LogGroup.Engine, "频道会话",
                            new { channelId, mode = isWorkingMode ? "working" : "express" });
                    else
                        sessionCtx = Signal.Begin(LogGroup.Engine, $"channel:{channelId}", "频道会话",
                            new { channelId, mode = isWorkingMode ? "working" : "express" });
                }

                // ④ Gate evaluation（会话内部：决定是否开闸）
                bool prepareResult;
                using (var gateSpan = Signal.Open(LogGroup.Engine, "闸门评估",
                    new { hasMessages = batch?.Count ?? 0, isWorkingMode }))
                {
                    prepareResult = await PrepareContextAsync(batch);
                    gateSpan.SetCloseDetail(new { passed = prepareResult });
                }

                if (!prepareResult)
                {
                    // 闸门拦截：结束当前会话
                    sessionCtx.Close(new { reason = "循环挂起" });
                    sessionCtx = null;
                    SignalContext.Restore(lifeCtx);
                    await componentHost.OnPauseAsync();
                    continue;
                }

                // ⑤ 单轮处理（上下文组装 → 模型调用 → 工具执行 → 后续决策）
                Interlocked.Exchange(ref _busyFlag, 1);
                try
                {
                    await componentHost.OnBeforeInvokeAsync();

                    using var roundSpan = Signal.Open(LogGroup.Engine, "处理轮次",
                        new { channelId, mode = isWorkingMode ? "working" : "express" });

                    // 上下文组装（轮次内）
                    await AssembleRoundContextAsync(batch);

                    var messages = BuildPromptMessages();
                    var mode = isWorkingMode ? EngineMode.Working : EngineMode.Express;

                    ModelOutput output;
                    using (var modelSpan = Signal.Open(LogGroup.Model, "AI模型调用",
                        new {
                            mode = mode.ToString(),
                            channelId,
                            core = agentCore.CoreName,
                            messageCount = messages.Count,
                            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
                            imageCount = currentImageEmbeds?.Count ?? 0
                        }))
                    {
                        output = await agentCore.InvokeAsync(messages, mode);
                        modelSpan.SetCloseDetail(new
                        {
                            isText = output.IsText,
                            hasToolCalls = output.HasToolCalls,
                            toolCount = output.ToolCalls?.Count ?? 0,
                            responseText = output.IsText ? output.Text : null,
                            thinking = output.Thinking,
                            toolCalls = output.ToolCalls?.Select(tc => new { tool = tc.Tool, inputs = tc.Inputs })
                        });
                    }

                    await componentHost.OnAfterInvokeAsync();
                    await ProcessResponseAsync(output);
                    DecideNext(output);
                    roundSpan.SetCloseDetail(new
                    {
                        mode = isWorkingMode ? "working" : "express",
                        isInWorkingSession,
                        hadSpeak = speakModule.HadSpeakThisRound,
                        toolCount = output.ToolCalls?.Count ?? 0
                    });
                    consecutiveFailures = 0;
                }
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
                        catch { }
                    }

                    if (consecutiveFailures >= ChannelMaxConsecutiveBeforeBackoff)
                    {
                        Signal.Warn(LogGroup.Engine, "连续失败退避", new { channelId, consecutiveFailures, backoffSeconds = ChannelBackoffSeconds });
                        await Task.Delay(TimeSpan.FromSeconds(ChannelBackoffSeconds));
                    }

                    isInWorkingSession = false;
                }
                finally
                {
                    // 会话结束：非 working 模式时关闭 session span
                    if (!isInWorkingSession && sessionCtx != null)
                    {
                        sessionCtx.Close(new { reason = "循环挂起" });
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

            // 关闭 ComponentHost
            if (componentHost != null)
                await componentHost.ShutdownAsync(ShutdownReason.Destroy);

            // 清理模块状态
            foreach (var m in modules) m.Reset();

            lifeCtx.Close(new { engineType = EngineType, channelId, reason = "cold_timeout" });
        }

        private void WireModuleCallbacks()
        {
            // 回调在每次 RunAsync 启动时绑定，生命周期 = 引擎实例
            // 实际的 lastMsg/lastSc 在 PrepareContextAsync 中更新
            speakModule.OnSpeak = async (rawText) =>
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
            };
            speakModule.OnSendMedia = async (type, text, attachments) =>
            {
                if (currentLastMsg == null || currentLastSc == null) return;
                unrespondedMessageCount = 0;
                using var mediaSpan = Signal.Open(LogGroup.Adapter, "发送媒体",
                    new { channelId, platform = currentLastMsg.Platform, type, text, attachmentCount = attachments?.Count ?? 0 });
                var sentId = await ctx.Adapters.SendMessageAsync(currentLastMsg.Platform, new OutgoingMessage
                {
                    ChannelId = currentLastMsg.ChannelId,
                    Content = text ?? "",
                    Attachments = attachments
                });
                mediaSpan.SetCloseDetail(new { messageId = sentId });
                var desc = $"[发送{type}]" + (string.IsNullOrEmpty(text) ? "" : $" {text}");
                await ctx.Session.SaveBotMessageAsync(currentLastSc.Channel.Id, desc, sentId);
            };
            signalDispatchModule.OnMemory = async (content) =>
            {
                if (currentLastSc == null) return;
                await ctx.MemorySvc.StoreAsync(content, currentLastSc.Person.Id, currentLastSc.Channel.Id,
                    type: MemoryType.Fact);
            };
            signalDispatchModule.OnSignal = async (signalName, payload) =>
            {
                ctx.EventBus.PublishSignal(signalName, payload);
                await Task.CompletedTask;
            };
            signalDispatchModule.OnReviewHint = async (content) =>
            {
                if (currentLastSc == null) return;
                await ctx.ReviewHints.CreateAsync(content, currentLastSc.Person.Id, currentLastSc.Channel.Id);
            };
            signalDispatchModule.OnAlert = async (reason) =>
            {
                if (currentLastSc == null) return;
                await HandleAlertAsync(currentLastSc.Person, currentLastSc, reason);
            };
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

        /// <summary>
        /// 统一上下文准备。每轮都重建 contextXml。
        /// 返回 false 表示本轮应跳过（静音/冲动值不够/无事可做）。
        /// </summary>
        private async Task<bool> PrepareContextAsync(
            List<(IncomingMessage Message, SessionContext Context)>? batch)
        {
            bool hasNewMessages = batch != null && batch.Count > 0;

            if (hasNewMessages)
            {
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
                        ToolContext = null!, // TODO: 接入 ToolContextImpl
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

                // 消费 pending 图片，检查描述缓存
                List<(string Path, string? Hash, string? Category)> pendingCopy;
                lock (bufferLock)
                {
                    pendingCopy = pendingImageInfos.Count > 0
                        ? new List<(string, string?, string?)>(pendingImageInfos) : new();
                    pendingImageInfos.Clear();
                }
                currentImageEmbeds = null;
                if (pendingCopy.Count > 0)
                    currentImageEmbeds = await ResolveImagePresentationAsync(pendingCopy);

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
                lastRoundCalls = null;
                lastRoundResults = null;
                loopControlModule.OnNewMessage();
                isInWorkingSession = false;

                // 同步频道工具 profile
                currentProfileName = ctx.ToolProfiles.GetProfileForChannel(currentLastMsg.ChannelId);
                var profileTools = ctx.ToolProfiles.GetActiveTools(currentProfileName);
                authorizedTools.Clear();
                foreach (var t in profileTools) authorizedTools.Add(t);

                // 冲动值扣减
                impulseTracker.ApplyPostResponseUpdate();

                // 记忆提取 + 信任增长
                TrackMemoryExtraction(batch, currentLastSc);
                await IncrementDailyProgressAsync(currentLastSc.Person);
            }
            else if (!isInWorkingSession)
            {
                return false;
            }
            // else: in working session, no new messages → continue processing

            return true;
        }

        /// <summary>统一重建上下文 XML（在轮次 span 内调用）。</summary>
        private async Task AssembleRoundContextAsync(
            List<(IncomingMessage Message, SessionContext Context)>? batch)
        {
            if (currentLastMsg == null || currentLastSc == null) return;
            currentParticipantSnapshot ??= new Dictionary<int, ParticipantInfo>(recentParticipants);

            using (var ctxSpan = Signal.Open(LogGroup.Memory, "组装对话上下文",
                new { channelId, personId = currentLastSc.Person.Id }))
            {
                var recentMessages = await ctx.Session.GetContextByChannelAsync(channelId);
                var effectiveBatch = batch ?? new List<(IncomingMessage, SessionContext)>();
                var (xml, imageEmbeds) = await contextBuilder.BuildContextXmlAsync(
                    effectiveBatch, recentMessages, currentParticipantSnapshot);

                // 图片直传列表：ContextBuilder 已根据规则收集
                if (imageEmbeds.Count > 0)
                {
                    currentImageEmbeds = imageEmbeds;
                }
                else
                {
                    currentImageEmbeds = null;
                }
                currentContextXml = xml;

                // 刷新记忆
                var memoryResults = await GetCachedMemoryAsync(currentLastSc, currentLastMsg.Content);
                memoryWindowModule.SetMemories(memoryResults);

                ctxSpan.SetCloseDetail(new
                {
                    recentMsgCount = recentMessages.Count,
                    memoryCount = memoryResults.Count,
                    hasImages = imageEmbeds.Count > 0
                });
            }
        }

        /// <summary>统一 prompt 构建。Express/Working 都走 PromptBuilder。</summary>
        private List<Models.Message> BuildPromptMessages()
        {
            var mode = isWorkingMode ? EngineMode.Working : EngineMode.Express;
            // 直接根据 WorkingCore 配置判断，不依赖当前 processor 状态（修复时序 bug）
            var useNative = isWorkingMode;

            // Working 模式：使用工具白名单过滤
            string toolDescs;
            string? nativeContext = null;
            if (isWorkingMode)
            {
                var channelToolFilter = new Func<ITool, bool>(tool =>
                {
                    var allowedTools = new HashSet<string>
                    {
                        "speak", "send_media", "thinking_notes", "memory", "pinboard", "retain_list", "task_management",
                        "mark_review_hint", "alert", "wait", "read_file", "write_file", "delegate_task", "adapter_action",
                        "view_image", "get_image_text"
                    };
                    return allowedTools.Contains(tool.Name);
                });

                if (useNative)
                {
                    // TODO: ToolFilter removed from AgentCore; will be replaced by ProfileManager
                    // agentCore.ToolFilter = channelToolFilter;
                    toolDescs = "";
                    // 原生模式下工具描述跳过，额外上下文单独注入
                    var ctxSb = new StringBuilder();
                    if (!string.IsNullOrEmpty(escalationReason))
                    {
                        ctxSb.AppendLine($"[升级任务] {escalationReason}");
                        escalationReason = null;
                    }
                    var botIdW = ctx.Adapters.GetBotPlatformId("qq");
                    if (!string.IsNullOrEmpty(botIdW))
                        ctxSb.AppendLine($"身份信息：你的QQ号是 {botIdW}。");
                    ctxSb.Append("[图片标记说明]\n上下文中的 <img/> 标记表示用户发送的图片。其中 desc/text 属性为自动生成的摘要，仅供快速参考，可能存在误差或遗漏。涉及具体内容时请使用工具查看原图或获取完整文字。");
                    nativeContext = ctxSb.ToString().TrimEnd();
                }
                else
                {
                    toolDescs = ToolRegistry.GenerateDescriptions(authorizedTools: authorizedTools, filter: channelToolFilter);
                    if (!string.IsNullOrEmpty(escalationReason))
                    {
                        toolDescs += $"\n\n[升级任务] {escalationReason}";
                        escalationReason = null;
                    }
                    var botIdW = ctx.Adapters.GetBotPlatformId("qq");
                    if (!string.IsNullOrEmpty(botIdW))
                        toolDescs += $"\n\n身份信息：你的QQ号是 {botIdW}。";
                    toolDescs += "\n\n[图片标记说明]\n上下文中的 <img/> 标记表示用户发送的图片。其中 desc/text 属性为自动生成的摘要，仅供快速参考，可能存在误差或遗漏。涉及具体内容时请使用工具查看原图或获取完整文字。";
                }
            }
            else
            {
                // Express 模式
                var useExpressNative = agentCore.UseNativeTools
                    && ToolRegistry.GetExpressToolDefinitions().Count > 0;

                if (useExpressNative)
                {
                    // 原生 Express 工具：工具定义通过 API 传递，这里只注入上下文提示
                    useNative = true;
                    toolDescs = "";
                    var ctxSb = new StringBuilder();
                    ctxSb.AppendLine("你当前处于轻量对话模式。需要执行复杂操作时调用 escalate 工具切换到工作模式。");
                    var botId = ctx.Adapters.GetBotPlatformId("qq");
                    if (!string.IsNullOrEmpty(botId))
                        ctxSb.AppendLine($"身份信息：你的QQ号是 {botId}，不要把自己的号当成别人的。");
                    ctxSb.AppendLine("轻量动作（直接在回复中使用，不需要切换模式）：\n- [POKE:对方QQ号] 戳一戳对方");

                    List<WatchRule> rules;
                    lock (watchRulesLock) { rules = new List<WatchRule>(watchRules); }
                    if (rules.Count > 0)
                    {
                        ctxSb.AppendLine("\n[关注规则]");
                        ctxSb.AppendLine("以下规则已激活，当消息匹配时会自动触发相应动作：");
                        foreach (var rule in rules)
                            ctxSb.AppendLine($"- {rule.Description}（模式：{rule.Pattern}，动作：{rule.Action}）");
                    }

                    ctxSb.Append("[图片标记说明]\n上下文中的 <img/> 标记表示用户发送的图片。其中 desc/text 属性为自动生成的摘要，仅供快速参考，可能存在误差或遗漏。涉及具体内容时请使用工具查看原图或获取完整文字。");
                    nativeContext = ctxSb.ToString().TrimEnd();
                }
                else
                {
                    // Fallback: 非 native 模式，使用文本能力摘要
                    var channelToolFilter = new Func<ITool, bool>(tool =>
                    {
                        var allowedTools = new HashSet<string>
                        {
                            "speak", "send_media", "thinking_notes", "memory", "pinboard", "retain_list", "task_management",
                            "mark_review_hint", "alert", "read_file", "write_file", "delegate_task", "adapter_action"
                        };
                        return allowedTools.Contains(tool.Name);
                    });
                    toolDescs = ToolRegistry.GenerateCapabilitySummary(filter: channelToolFilter);
                    var botId = ctx.Adapters.GetBotPlatformId("qq");
                    var pokeHint = "\n\n轻量动作（直接在回复中使用，不需要切换模式）：\n- [POKE:对方QQ号] 戳一戳对方";
                    if (!string.IsNullOrEmpty(botId))
                        pokeHint += $"\n\n身份信息：你的QQ号是 {botId}，不要把自己的号当成别人的。";
                    toolDescs += pokeHint;

                    List<WatchRule> rules;
                    lock (watchRulesLock) { rules = new List<WatchRule>(watchRules); }
                    if (rules.Count > 0)
                    {
                        var sb = new StringBuilder();
                        sb.AppendLine("\n[关注规则]");
                        sb.AppendLine("以下规则已激活，当消息匹配时会自动触发相应动作：");
                        foreach (var rule in rules)
                            sb.AppendLine($"- {rule.Description}（模式：{rule.Pattern}，动作：{rule.Action}）");
                        toolDescs += "\n" + sb.ToString();
                    }

                    toolDescs += "\n\n[图片标记说明]\n上下文中的 <img/> 标记表示用户发送的图片。其中 desc/text 属性为自动生成的摘要，仅供快速参考，可能存在误差或遗漏。涉及具体内容时请使用工具查看原图或获取完整文字。";
                }
            }

            var messages = promptBuilder.BuildRoundMessages(
                toolDescs, currentContextXml!, modules, mode,
                lastRoundResults, lastRoundCalls,
                currentImageEmbeds, useNative);

            // 原生模式下：额外上下文（Bot ID、升级原因等）在 PromptBuilder 跳过工具描述后单独注入
            if (useNative && nativeContext != null)
            {
                // 插入到 contextXml 消息之后、模块消息之前（index = 工具描述被跳过后的第2个消息之后）
                messages.Insert(1, new Models.Message { Role = "user", Content = nativeContext });
            }

            // Component 系统 prompt 注入
            if (componentHost != null)
            {
                var groups = ToolListFormatter.CollectGroups(componentHost, ctx.GlobalComponentHost);
                var toolOverview = ToolListFormatter.BuildToolOverviewSection(groups);
                if (toolOverview != null)
                    messages.Add(new Models.Message { Role = "user", Content = toolOverview });

                var componentSections = componentHost.BuildPromptSections();
                foreach (var section in componentSections)
                    messages.Add(new Models.Message { Role = "user", Content = section });

                var globalSections = ctx.GlobalComponentHost?.BuildPromptSections(
                    new LoopInfo(channelId.ToString(), "channel")) ?? new();
                foreach (var section in globalSections)
                    messages.Add(new Models.Message { Role = "user", Content = section });
            }

            // 拦截器注入的额外提示
            if (interceptorInjections.Count > 0)
            {
                var injection = string.Join("\n", interceptorInjections);
                messages.Add(new Models.Message { Role = "user", Content = injection });
            }

            // 图片只在首次使用后清除
            currentImageEmbeds = null;
            return messages;
        }

        /// <summary>统一输出处理。Express 发文本，Working 执行工具。</summary>
        private async Task ProcessResponseAsync(ModelOutput output)
        {
            if (currentLastMsg == null || currentLastSc == null || currentParticipantSnapshot == null) return;

            if (output.IsText)
            {
                var text = output.Text!;

                // [ALERT] 检测 (fallback，非 native 模式)
                if (!output.HasToolCalls && text.Contains("[ALERT]"))
                {
                    var reason = text.Replace("[ALERT]", "").Replace("[ESCALATE]", "").Trim();
                    await HandleAlertAsync(currentLastSc.Person, currentLastSc,
                        string.IsNullOrEmpty(reason) ? "聊天中触发" : reason);
                    text = text.Replace("[ALERT]", "").Trim();
                }

                // 发送文本（fallback 模式下 ESCALATE 前的部分）
                var sendText = (!output.HasToolCalls && text.Contains("[ESCALATE]"))
                    ? text.Split("[ESCALATE]")[0].Trim()
                    : text;

                // [POKE:uid] 轻量动作：提取并执行戳一戳
                sendText = await ProcessPokeMarkers(sendText, currentLastMsg);

                if (!string.IsNullOrEmpty(sendText))
                    await SendSegmentsAsync(sendText, currentLastMsg, currentLastSc, currentParticipantSnapshot);

                // Fire-and-forget: 静默执行 Express 工具（结果不回注）
                if (output.HasToolCalls)
                {
                    Tool.Core.ManageComponentsTool.CurrentLoop.Value =
                        new Tool.Core.ManageComponentsTool.LoopContext(currentProfileName, $"channel-{channelId}");
                    var executor = new ToolExecutor(authorizedTools: null);
                    await executor.ExecuteAsync(output.ToolCalls!);

                    foreach (var tc in output.ToolCalls!)
                    {
                        var inputSummary = tc.Inputs.Count > 0 ? string.Join(", ", tc.Inputs).Truncate(80) : "";
                    }
                }
            }
            else
            {
                // Working: 执行工具
                isInWorkingSession = true;
                if (lastRoundCalls == null) consecutiveExternalTriggers++;
                var toolCalls = output.ToolCalls!;

                if (toolCalls.Count == 0)
                {
                    EndWorkingSession();
                    return;
                }

                speakModule.ResetRound();
                Tool.Core.ManageComponentsTool.CurrentLoop.Value =
                    new Tool.Core.ManageComponentsTool.LoopContext(currentProfileName, $"channel-{channelId}");
                var executor = new ToolExecutor(authorizedTools: authorizedTools);
                executor.OnToolExecuted = async (call, result) =>
                {
                    var toolDef = ToolRegistry.Get(call.Tool);
                    bus.Publish(new ToolExecutedEvent(call, result, toolDef));
                    await Task.CompletedTask;
                };

                List<ToolResult> results;
                using (var toolSpan = Signal.Open(LogGroup.Tool, "执行工具",
                    new { toolCount = toolCalls.Count, tools = string.Join(",", toolCalls.Select(c => c.Tool)) }))
                {
                    results = await executor.ExecuteAsync(toolCalls);
                    toolSpan.SetCloseDetail(new
                    {
                        successCount = results.Count(r => r.Status == "ok"),
                        errorCount = results.Count(r => r.Error != null)
                    });
                }

                lastRoundCalls = toolCalls;
                lastRoundResults = results;
                loopControlModule.AdvanceRound(speakModule.HadSpeakThisRound);

                foreach (var tc in toolCalls)
                {
                    var inputSummary = tc.Inputs.Count > 0 ? string.Join(", ", tc.Inputs).Truncate(80) : "";
                    var r = results[toolCalls.IndexOf(tc)];
                }
            }
        }

        /// <summary>统一后续决策。决定是否继续循环、切换模式、或 idle。</summary>
        private void DecideNext(ModelOutput output)
        {
            if (output.IsText)
            {
                // Express 工具路径：检查 escalate 工具调用
                if (output.HasToolCalls && output.ToolCalls!.Any(c => c.Tool == "escalate"))
                {
                    var call = output.ToolCalls!.First(c => c.Tool == "escalate");
                    escalationReason = call.Inputs.Count > 0 && !string.IsNullOrWhiteSpace(call.Inputs[0])
                        ? call.Inputs[0] : null;
                    isWorkingMode = true;
                    isInWorkingSession = true;
                    consecutiveExternalTriggers = 0;
                    Signal.Event(LogGroup.Engine, "模式切换", new { channelId, from = "Express", to = "Working", reason = escalationReason ?? "工具调用" });
                    gate.Signal();
                }
                // Fallback: 非 native 模式仍解析文本标记
                else if (!output.HasToolCalls && output.Text!.Contains("[ESCALATE]"))
                {
                    var parts = output.Text!.Split("[ESCALATE]", 2);
                    escalationReason = parts.Length > 1 ? parts[1].Trim() : null;
                    isWorkingMode = true;
                    isInWorkingSession = true;
                    consecutiveExternalTriggers = 0;
                    Signal.Event(LogGroup.Engine, "模式切换", new { channelId, from = "Express", to = "Working", reason = escalationReason ?? "文本标记" });
                    gate.Signal();
                }
            }
            else
            {
                if (output.ToolCalls == null || output.ToolCalls.Count == 0) return;

                // 安全上限
                if (loopControlModule.IsMaxSilentReached || loopControlModule.IsMaxRoundsReached)
                {
                    if (loopControlModule.IsMaxSilentReached && speakModule.OnSpeak != null)
                        speakModule.OnSpeak("（工作暂停，等待回应后继续）").GetAwaiter().GetResult();

                    EndWorkingSession();
                    return;
                }

                // 显式结束信号
                bool hasWait = output.ToolCalls.Any(c => c.Tool == "wait");
                if (hasWait)
                {
                    EndWorkingSession();
                    return;
                }

                // 只调了输出工具 → 标记，下一轮提示确认
                var outputOnlyTools = new HashSet<string> { "speak", "send_media" };
                bool isOutputOnly = output.ToolCalls.All(c => outputOnlyTools.Contains(c.Tool));
                loopControlModule.WasOutputOnly = isOutputOnly;

                gate.Signal();
            }
        }

        private void EndWorkingSession()
        {
            Signal.Event(LogGroup.Engine, "Working会话结束", new
            {
                channelId,
                totalRounds = loopControlModule.TotalRounds,
                silentRounds = loopControlModule.SilentRounds,
                hadSpeak = speakModule.HadSpeakThisRound
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
            LastExtractedMessageId = lastExtractedMessageId < 0 ? 0 : lastExtractedMessageId,
            LatestMessageId = latestMessageId,
            TotalMessageCount = totalMessageCount,
            ExtractedMessageCount = extractedMessageCount,
            ExtractionRunning = extractionRunning,
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



        // ---- 记忆 ----

        private void TrackMemoryExtraction(
            List<(IncomingMessage Message, SessionContext Context)> messages, SessionContext sc)
        {
            this.lastContext = sc;
            processedMessageCount += messages.Count;
            unrespondedMessageCount += messages.Count;

            if (!channelConfig.AutoExtractionEnabled) return;
            if (extractionRunning) return;
            _ = RunExtractionAsync(sc);
        }

        private async Task<List<ScoredMemory>> GetCachedMemoryAsync(SessionContext context, string query)
        {
            int personId = context.Person.Id;

            if (memoryCache.TryGetValue(personId, out var cached) &&
                (DateTime.Now - cached.Time).TotalSeconds < MemoryCacheTtlSeconds)
            {
                return cached.Results;
            }

            try
            {
                // 整体 15s 超时保护，防止 API 调用拖慢回复
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var task = FetchMemoryAsync(personId, context.Channel.Id, query);
                var completed = await Task.WhenAny(task, Task.Delay(15000, cts.Token));

                if (completed == task)
                {
                    cts.Cancel();
                    var results = await task;
                    memoryCache[personId] = (results, DateTime.Now);
                    return results;
                }

                return new List<ScoredMemory>();
            }
            catch
            {
                return new List<ScoredMemory>();
            }
        }

        private async Task<List<ScoredMemory>> FetchMemoryAsync(int personId, int channelId, string query)
        {
            var intent = await GetCachedQueryIntentAsync();

            if (intent != null && (intent.Keywords.Count > 0 || intent.Subjects.Count > 0))
            {
                return await ctx.MemorySvc.RecallAsync(
                    personId, channelId,
                    query, intent, topK: 10, includeLinks: true, includePersona: true);
            }
            else
            {
                return await ctx.MemorySvc.RecallAsync(
                    personId, channelId,
                    query, topK: 10, includeLinks: true, includePersona: true);
            }
        }

        private async Task<MemoryQueryIntent?> GetCachedQueryIntentAsync()
        {
            if (cachedQueryIntent != null &&
                (DateTime.Now - cachedQueryIntentTime).TotalSeconds < QueryIntentCacheTtlSeconds)
            {
                return cachedQueryIntent;
            }

            try
            {
                var recent = await ctx.Session.GetContextByChannelAsync(channelId, limit: 5);
                if (recent.Count < 1) return null;

                var lines = recent.Select(m =>
                {
                    var name = m.IsFromBot ? "Lilara" : (m.SenderName ?? $"User{m.UserId}");
                    return $"{name}: {m.Content}";
                }).ToList();

                var core = new MemoryQueryCore();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var intentTask = core.ExtractIntentAsync(lines);
                var completed = await Task.WhenAny(intentTask, Task.Delay(5000, cts.Token));

                if (completed == intentTask)
                {
                    cts.Cancel();
                    var intent = await intentTask;
                    cachedQueryIntent = intent;
                    cachedQueryIntentTime = DateTime.Now;
                    return intent;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        public async Task TriggerLurkingExtractionAsync()
        {
            if (lastContext == null) return;
            await RunExtractionAsync(lastContext);
        }

        public void SetAutoExtraction(bool enabled)
        {
            channelConfig.AutoExtractionEnabled = enabled;
            ChannelStateManager.SaveConfig(channelId, channelConfig);
        }

        public void CancelExtraction()
        {
            extractionCts?.Cancel();
        }

        private async Task RunExtractionAsync(SessionContext context)
        {
            if (extractionRunning) return;
            extractionRunning = true;
            extractionCts = new CancellationTokenSource();
            var ct = extractionCts.Token;
            try
            {
                // 首次运行时从 DB 加载持久化进度
                if (lastExtractedMessageId < 0)
                {
                    var channel = await ctx.Session.GetChannelAsync(channelId);
                    lastExtractedMessageId = channel?.LastExtractedMessageId ?? 0;
                }

                totalMessageCount = await ctx.Session.GetMessageCountByChannelAsync(channelId);
                extractedMessageCount = await ctx.Session.GetMessageCountUpToAsync(
                    channelId, lastExtractedMessageId);

                while (!ct.IsCancellationRequested)
                {
                    totalMessageCount = await ctx.Session.GetMessageCountByChannelAsync(channelId);
                    extractedMessageCount = await ctx.Session.GetMessageCountUpToAsync(
                        channelId, lastExtractedMessageId);

                    // 取新消息（上次提取之后的）
                    var newMessages = await ctx.Session.GetMessagesAfterIdAsync(
                        channelId, lastExtractedMessageId, limit: 50);
                    if (newMessages.Count < 2) break;

                    // 更新最新消息 ID（用于 WebUI 进度显示）
                    latestMessageId = newMessages[^1].Id;

                    // 根据上次回复时间判断活跃/潜水阈值
                    bool isActive = LastCompletionTime != null
                        && (DateTime.Now - LastCompletionTime.Value).TotalMinutes < 5;
                    int threshold = isActive
                        ? channelConfig.ActiveExtractionThreshold
                        : channelConfig.LurkingExtractionThreshold;

                    if (newMessages.Count < threshold) break;

                    // 取旧消息做参考上下文
                    var contextMessages = lastExtractedMessageId > 0
                        ? await ctx.Session.GetMessagesBeforeIdAsync(channelId, lastExtractedMessageId, limit: 20)
                        : new List<UserMessage>();

                    // 取近期记忆做去重
                    var recentMems = await ctx.TempMemories.GetRecentByChannelAsync(channelId, 10);
                    var recentMemContents = recentMems.Count > 0
                        ? recentMems.ConvertAll(m => m.Content)
                        : null;

                    // 构造对话行
                    var contextLines = contextMessages.Select(FormatMessageLine).ToList();
                    var newLines = newMessages.Select(FormatMessageLine).ToList();

                    var participantNames = recentParticipants.Values
                        .Select(p => p.DisplayName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Distinct().ToList();
                    if (participantNames.Count > 0)
                        contextLines.Insert(0, $"[群聊参与者: {string.Join("、", participantNames)}]");

                    // 调用提取
                    var core = new MemoryExtractionCore();
                    var results = await core.ExtractAsync(contextLines, newLines, recentMemContents);

                    int count = 0;
                    foreach (var item in results)
                    {
                        if (item.Type == "knowledge")
                        {
                            await ctx.MemorySvc.StoreAsync(item.Content,
                                personId: null, channelId: null,
                                confidence: item.Confidence,
                                type: MemoryType.Knowledge, subject: item.Subject);
                        }
                        else if (item.Type == "feedback" && item.Sentiment != null)
                        {
                            int personId = ResolveAboutToPersonId(item.About) ?? context.Person.Id;
                            await ctx.MemorySvc.ApplyFeedbackAsync(
                                personId, item.Content, item.Sentiment, item.Correction);
                        }
                        else
                        {
                            int personId = ResolveAboutToPersonId(item.About) ?? context.Person.Id;
                            string memType = item.Type ?? MemoryType.Fact;
                            await ctx.MemorySvc.StoreAsync(item.Content,
                                personId, context.Channel.Id,
                                confidence: item.Confidence,
                                type: memType, subject: item.Subject);
                        }
                        count++;
                    }

                    // 更新进度标记并持久化
                    lastExtractedMessageId = newMessages[^1].Id;
                    await ctx.Session.UpdateExtractionProgressAsync(channelId, lastExtractedMessageId);
                    extractedMessageCount = await ctx.Session.GetMessageCountUpToAsync(
                        channelId, lastExtractedMessageId);

                    if (count > 0)

                    // 如果这批不满 50 条，说明已经追上了
                    if (newMessages.Count < 50) break;
                }
            }
            catch (Exception ex)
            {
            }
            finally
            {
                extractionRunning = false;
            }
        }

        private static string FormatMessageLine(UserMessage m)
        {
            if (m.IsFromBot) return $"Lilara: {m.Content}";
            var name = !string.IsNullOrEmpty(m.SenderName) ? m.SenderName : "用户";
            return $"{name}(#{m.UserId}): {m.Content}";
        }

        private async Task ExtractMemoryAsync(SessionContext context)
        {
            await RunExtractionAsync(context);
        }

        private int? ResolveAboutToPersonId(string? about)
        {
            if (string.IsNullOrEmpty(about)) return null;

            // 优先解析 #userId 格式
            if (about.StartsWith('#') && int.TryParse(about[1..], out var userId))
            {
                if (recentParticipants.TryGetValue(userId, out var info))
                    return info.PersonId;
            }

            // 内容中可能包含 "名字(#id)" 格式，提取 id
            var hashIdx = about.IndexOf('#');
            if (hashIdx >= 0)
            {
                var idPart = about[(hashIdx + 1)..].TrimEnd(')');
                if (int.TryParse(idPart, out var uid) && recentParticipants.TryGetValue(uid, out var info2))
                    return info2.PersonId;
            }

            // fallback: 名字匹配
            foreach (var (_, p) in recentParticipants)
                if (p.DisplayName.Equals(about, StringComparison.OrdinalIgnoreCase))
                    return p.PersonId;

            return null;
        }

        private static string? FormatMemory(List<ScoredMemory>? results, int topK)
        {
            if (results == null || results.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (var m in results.Take(topK))
            {
                if (m.Confidence == "low")
                    sb.AppendLine($"- {m.Content}（不太确定）");
                else
                    sb.AppendLine($"- {m.Content}");
            }
            return sb.ToString().TrimEnd();
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

        private Task<List<ImageEmbed>?> ResolveImagePresentationAsync(
            List<(string Path, string? Hash, string? Category)> images)
        {
            foreach (var (path, hash, category) in images)
            {
                if (!string.IsNullOrEmpty(hash))
                    _ = ImageStorage.IncrementSeenCountAsync(hash);
            }
            return Task.FromResult<List<ImageEmbed>?>(null);
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


                // 发送通知
                ctx.TaskBridge.PostNotification(new Notification
                {
                    Type = NotificationType.WatchHit,
                    SourceId = $"channel_{channelId}",
                    Summary = $"规则「{rule.RuleId}」命中：{rule.Description}\n" +
                              $"消息：{msg.Content.Substring(0, Math.Min(50, msg.Content.Length))}..."
                });

                // 根据动作执行
                switch (rule.Action)
                {
                    case WatchAction.Notify:
                        // 仅通知，不打断
                        break;

                    case WatchAction.Interrupt:
                        // 打断当前任务，立即响应
                        if (rule.AutoResponse)
                        {
                            gate.Signal();
                        }
                        break;

                    case WatchAction.Escalate:
                        // 升级到系统循环
                        var task = new SystemTask
                        {
                            SourceChannelId = channelId,
                            Description = $"关注规则「{rule.RuleId}」触发：{rule.Description}",
                            ContextSummary = msg.Content,
                            RequestingPersonId = sc.Person.Id,
                            Priority = 5
                        };
                        _ = ctx.TaskBridge.SubmitTaskAsync(task, TimeSpan.FromMinutes(5));
                        break;
                }
            }
        }
    }
}
