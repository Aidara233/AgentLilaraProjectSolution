using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Command;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Memory;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Tool;

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
        private readonly ThinkingNotesModule thinkingNotesModule = new();
        private readonly TaskListModule taskListModule = new();
        private readonly PinboardModule pinboardModule = new();
        private readonly RetainListModule retainListModule = new();
        private readonly MemoryWindowModule memoryWindowModule = new();
        private readonly LoopControlModule loopControlModule = new();
        private readonly SignalDispatchModule signalDispatchModule = new();
        private List<EngineModule> modules = null!;

        // 已处理消息标记
        private readonly LinkedList<long> processedTicks = new();
        private const int MaxProcessedTicksWindow = 50;

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
        private bool lurkingExtractionRunning = false;
        private SessionContext? lastContext;

        // TrustProgress 每日自动增长跟踪
        private readonly Dictionary<int, (DateTime Date, float Accumulated)> dailyProgressTracker = new();

        // 授权工具集（会话级）
        private readonly HashSet<string> authorizedTools = new();

        // Express/Working 自适应切换
        private bool isWorkingMode = false;
        private int consecutiveExternalTriggers = 0;
        private const int WorkingToExpressThreshold = 3;

        // Working 会话状态（跨闸门轮次保持）
        private string? currentContextXml;
        private List<string>? currentImagePaths;
        private Dictionary<int, ParticipantInfo>? currentParticipantSnapshot;
        private IncomingMessage? currentLastMsg;
        private SessionContext? currentLastSc;
        private List<ToolCall>? lastRoundCalls;
        private List<ToolResult>? lastRoundResults;
        private bool isInWorkingSession = false;

        // 缓冲定时器
        private CancellationTokenSource? _bufferTimerCts;

        // 未消费的图片路径
        private readonly List<string> pendingImagePaths = new();

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

            buffer.Add((initialMessage, initialContext));
            CollectImagePaths(initialMessage);
            recentParticipants.TryAdd(initialContext.User.Id, ParticipantInfo.From(initialContext.User, initialContext.Person, initialMessage));
            impulseTracker.Accumulate(initialMessage, recentParticipants.Count, initialContext);
            InitModules();
            ScheduleBufferSignal();

            FrameworkLogger.Log("ChannelEngine", $"创建: channelId={channelId}, affinity={impulseTracker.ChannelAffinity:F2}");
        }

        private void InitModules()
        {
            modules = new List<EngineModule>
            {
                speakModule, thinkingNotesModule, taskListModule, pinboardModule,
                retainListModule, memoryWindowModule, loopControlModule, signalDispatchModule
            };
            foreach (var m in modules) m.Attach(bus);
        }

        /// <summary>由 SpawnCheck 调用，将新消息加入缓冲。</summary>
        public void EnqueueMessage(IncomingMessage msg, SessionContext sc)
        {
            lock (bufferLock)
            {
                buffer.Add((msg, sc));
                lastBufferTime = DateTime.Now;
                CollectImagePaths(msg);
            }
            recentParticipants.AddOrUpdate(
                sc.User.Id,
                ParticipantInfo.From(sc.User, sc.Person, msg),
                (_, _) => ParticipantInfo.From(sc.User, sc.Person, msg));
            impulseTracker.Accumulate(msg, recentParticipants.Count, sc);
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


        public async Task RunAsync()
        {
            FrameworkLogger.Log("ChannelEngine", $"启动: channelId={channelId}");
            WireModuleCallbacks();

            while (IsAlive)
            {
                // ① WaitGate
                var triggered = await gate.WaitAsync(
                    TimeSpan.FromSeconds(ctx.ImpulseConfig.ColdTimeoutSeconds));

                if (!triggered)
                {
                    if (processedMessageCount > 0 && lastContext != null)
                        await ExtractMemoryAsync(lastContext);
                    FrameworkLogger.Log("ChannelEngine", $"冷却退出: channelId={channelId}");
                    IsAlive = false;
                    break;
                }

                impulseTracker.Decay();

                // ② CollectBuffer
                var batch = CollectBuffer();

                // ③ PrepareContext
                if (!await PrepareContextAsync(batch)) continue;

                // ④⑤⑥⑦ BuildPrompt → CallModel → ProcessResponse → DecideNext
                Interlocked.Exchange(ref _busyFlag, 1);
                try
                {
                    var messages = BuildPromptMessages();
                    var mode = isWorkingMode ? EngineMode.Working : EngineMode.Express;
                    var output = await agentCore.InvokeAsync(messages, mode);
                    await ProcessResponseAsync(output);
                    DecideNext(output);
                }
                catch (Exception ex)
                {
                    FrameworkLogger.LogError("ChannelEngine", ex, $"channelId={channelId}");
                    if (currentLastMsg != null)
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
                    isInWorkingSession = false;
                }
                finally
                {
                    if (!isInWorkingSession)
                    {
                        Interlocked.Exchange(ref _busyFlag, 0);
                        Interlocked.Exchange(ref _completionTicks, DateTime.Now.Ticks);
                    }
                }
            }

            // 清理模块状态
            foreach (var m in modules) m.Reset();
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
                var sentId = await ctx.Adapters.SendMessageAsync(currentLastMsg.Platform, new OutgoingMessage
                {
                    ChannelId = currentLastMsg.ChannelId,
                    Content = content,
                    ReplyTo = replyTo,
                    Mentions = mentions
                });
                await ctx.Session.SaveBotMessageAsync(currentLastSc.Channel.Id, content, sentId);
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

                FrameworkLogger.Log("ChannelEngine",
                    $"处理批次: channelId={channelId}, 消息数={batch.Count}, " +
                    $"user={currentLastSc.User.PlatformId} person={currentLastSc.Person.Id}");

                if (ctx.MuteMode)
                {
                    TrackMemoryExtraction(batch, currentLastSc);
                    return false;
                }
                if (!impulseTracker.ShouldRespond(batch, LastCompletionTime)) return false;

                // 消费 pending 图片
                lock (bufferLock)
                {
                    currentImagePaths = pendingImagePaths.Count > 0
                        ? new List<string>(pendingImagePaths) : null;
                    pendingImagePaths.Clear();
                }

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
                    var isTask = await preprocessingCore.IsTaskAsync(
                        batch.Select(b => b.Message.Content).LastOrDefault() ?? "");
                    FrameworkLogger.Log("ChannelEngine", $"分类结果: {(isTask ? "任务" : "聊天")}");
                    if (isTask) { isWorkingMode = true; consecutiveExternalTriggers = 0; }
                }

                // 重置 Working 轮次状态
                lastRoundCalls = null;
                lastRoundResults = null;
                loopControlModule.OnNewMessage();
                isInWorkingSession = false;

                // 同步频道授权
                var granted = AuthStore.GetGranted(currentLastMsg.ChannelId);
                authorizedTools.Clear();
                foreach (var t in granted) authorizedTools.Add(t);

                // 设置 DelegateTaskTool 的上下文
                var delegateTool = ToolRegistry.Get("委派任务") as DelegateTaskTool;
                if (delegateTool != null)
                {
                    delegateTool.SetContext(currentLastSc.Channel.Id, currentLastSc.Person.Id);
                }

                // 冲动值扣减 + expectation 更新
                bool triggeredByMention = batch.Any(b => b.Message.IsMentioned || b.Message.IsPrivate);
                impulseTracker.ApplyPostResponseUpdate(triggeredByMention);

                // 记忆提取 + 信任增长
                TrackMemoryExtraction(batch, currentLastSc);
                await IncrementDailyProgressAsync(currentLastSc.Person);
            }
            else if (!isInWorkingSession)
            {
                return false;
            }
            else
            {
                FrameworkLogger.Log("ChannelEngine",
                    $"Working 续轮: channelId={channelId}, round={loopControlModule.TotalRounds}");
            }

            // 每轮统一重建上下文
            if (currentLastMsg == null || currentLastSc == null) return false;
            currentParticipantSnapshot ??= new Dictionary<int, ParticipantInfo>(recentParticipants);

            var recentMessages = await ctx.Session.GetContextByChannelAsync(channelId);
            var effectiveBatch = batch ?? new List<(IncomingMessage, SessionContext)>();
            var (xml, quotedImagePaths) = await contextBuilder.BuildContextXmlAsync(
                effectiveBatch, recentMessages, currentParticipantSnapshot);

            if (quotedImagePaths.Count > 0)
            {
                currentImagePaths ??= new List<string>();
                currentImagePaths.AddRange(quotedImagePaths);
            }
            if (currentImagePaths?.Count > 0)
            {
                var prefix = currentImagePaths.Count == 1
                    ? "（用户发送了一张图片）"
                    : $"（用户发送了{currentImagePaths.Count}张图片）";
                xml += $"\n\n{prefix}";
            }
            currentContextXml = xml;

            // 刷新记忆
            var memoryResults = await GetCachedMemoryAsync(currentLastSc, currentLastMsg.Content);
            memoryWindowModule.SetMemories(memoryResults);

            return true;
        }

        /// <summary>统一 prompt 构建。Express/Working 都走 PromptBuilder。</summary>
        private List<Models.Message> BuildPromptMessages()
        {
            var mode = isWorkingMode ? EngineMode.Working : EngineMode.Express;

            // Working 模式：使用工具白名单过滤
            string toolDescs;
            if (isWorkingMode)
            {
                var channelToolFilter = new Func<ITool, bool>(tool =>
                {
                    var allowedTools = new HashSet<string>
                    {
                        "说话", "思考笔记", "记忆", "便签板", "缓存管理", "任务管理",
                        "标记复盘", "报警", "继续", "文件读取", "委派任务"
                    };
                    return allowedTools.Contains(tool.Name);
                });
                toolDescs = ToolRegistry.GenerateDescriptions(authorizedTools: authorizedTools, filter: channelToolFilter);
            }
            else
            {
                // Express 模式：使用能力摘要（也应用过滤器）
                var channelToolFilter = new Func<ITool, bool>(tool =>
                {
                    var allowedTools = new HashSet<string>
                    {
                        "说话", "思考笔记", "记忆", "便签板", "缓存管理", "任务管理",
                        "标记复盘", "报警", "继续", "文件读取", "委派任务"
                    };
                    return allowedTools.Contains(tool.Name);
                });
                toolDescs = ToolRegistry.GenerateCapabilitySummary(filter: channelToolFilter);
            }

            // Phase 6: 注入关注规则（仅 Express 模式）
            if (!isWorkingMode)
            {
                List<WatchRule> rules;
                lock (watchRulesLock)
                {
                    rules = new List<WatchRule>(watchRules);
                }

                if (rules.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("\n[关注规则]");
                    sb.AppendLine("以下规则已激活，当消息匹配时会自动触发相应动作：");
                    foreach (var rule in rules)
                    {
                        sb.AppendLine($"- {rule.Description}（模式：{rule.Pattern}，动作：{rule.Action}）");
                    }
                    toolDescs += "\n" + sb.ToString();
                }
            }

            var messages = promptBuilder.BuildRoundMessages(
                toolDescs, currentContextXml!, modules, mode,
                lastRoundResults, lastRoundCalls,
                currentImagePaths);

            // 图片只在首次使用后清除
            currentImagePaths = null;
            return messages;
        }

        /// <summary>统一输出处理。Express 发文本，Working 执行工具。</summary>
        private async Task ProcessResponseAsync(ModelOutput output)
        {
            if (currentLastMsg == null || currentLastSc == null || currentParticipantSnapshot == null) return;

            if (output.IsText)
            {
                var text = output.Text!;

                // [ALERT] 检测
                if (text.Contains("[ALERT]"))
                {
                    FrameworkLogger.Log("ChannelEngine", $"Express 触发报警: channelId={channelId}");
                    var reason = text.Replace("[ALERT]", "").Replace("[ESCALATE]", "").Trim();
                    await HandleAlertAsync(currentLastSc.Person, currentLastSc,
                        string.IsNullOrEmpty(reason) ? "聊天中触发" : reason);
                    text = text.Replace("[ALERT]", "").Trim();
                }

                // 发送文本（ESCALATE 前的部分）
                var sendText = text.Contains("[ESCALATE]")
                    ? text.Split("[ESCALATE]")[0].Trim()
                    : text;

                if (!string.IsNullOrEmpty(sendText))
                    await SendSegmentsAsync(sendText, currentLastMsg, currentLastSc, currentParticipantSnapshot);
            }
            else
            {
                // Working: 执行工具
                isInWorkingSession = true;
                if (lastRoundCalls == null) consecutiveExternalTriggers++;
                var toolCalls = output.ToolCalls!;

                if (toolCalls.Count == 0)
                {
                    FrameworkLogger.Log("ChannelEngine", $"Working idle: channelId={channelId}");
                    EndWorkingSession();
                    return;
                }

                speakModule.ResetRound();
                var executor = new ToolExecutor(authorizedTools: authorizedTools);
                executor.OnToolExecuted = async (call, result) =>
                {
                    var toolDef = ToolRegistry.Get(call.Tool);
                    bus.Publish(new ToolExecutedEvent(call, result, toolDef));
                    await Task.CompletedTask;
                };
                var results = await executor.ExecuteAsync(toolCalls);

                lastRoundCalls = toolCalls;
                lastRoundResults = results;
                loopControlModule.AdvanceRound(speakModule.HadSpeakThisRound);

                FrameworkLogger.Log("ChannelEngine",
                    $"Working 执行: channelId={channelId}, 工具数={toolCalls.Count}, " +
                    $"spoke={speakModule.HadSpeakThisRound}, round={loopControlModule.TotalRounds}");
            }
        }

        /// <summary>统一后续决策。决定是否继续循环、切换模式、或 idle。</summary>
        private void DecideNext(ModelOutput output)
        {
            if (output.IsText)
            {
                if (output.Text!.Contains("[ESCALATE]"))
                {
                    FrameworkLogger.Log("ChannelEngine", $"Express→Working 升级: channelId={channelId}");
                    isWorkingMode = true;
                    consecutiveExternalTriggers = 0;
                    isInWorkingSession = false;
                    gate.Signal();
                }
            }
            else
            {
                if (output.ToolCalls == null || output.ToolCalls.Count == 0) return;

                bool hasContinue = output.ToolCalls.Any(c => ToolRegistry.Get(c.Tool)?.ContinueLoop == true);

                if (hasContinue && !loopControlModule.IsMaxSilentReached && !loopControlModule.IsMaxRoundsReached)
                {
                    FrameworkLogger.Log("ChannelEngine",
                        $"ContinueLoop 自唤醒: channelId={channelId}, round={loopControlModule.TotalRounds}");
                    gate.Signal();
                }
                else
                {
                    if (loopControlModule.IsMaxSilentReached && speakModule.OnSpeak != null)
                        speakModule.OnSpeak("（工作暂停，等待回应后继续）").GetAwaiter().GetResult();

                    FrameworkLogger.Log("ChannelEngine",
                        $"Working 结束: rounds={loopControlModule.TotalRounds}, " +
                        $"silent={loopControlModule.SilentRounds}, channelId={channelId}");
                    EndWorkingSession();
                }
            }
        }

        private void EndWorkingSession()
        {
            isInWorkingSession = false;
            if (consecutiveExternalTriggers >= WorkingToExpressThreshold)
            {
                FrameworkLogger.Log("ChannelEngine",
                    $"Working→Express 回退: 连续{consecutiveExternalTriggers}次外部触发, channelId={channelId}");
                isWorkingMode = false;
                consecutiveExternalTriggers = 0;
            }
        }
        public void OnEvent(EngineEvent e) { }

        public void RequestStop()
        {
            IsAlive = false;
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
            MessageRate = impulseTracker.MessageRate,
            Expectation = impulseTracker.Expectation,
            Reality = impulseTracker.Reality,
            ChannelAffinity = channelConfig.Affinity,
            Importance = channelConfig.Importance,
            ExtractionInterval = channelConfig.ExtractionInterval,
            UnrespondedMessageCount = unrespondedMessageCount,
            ConsecutiveExternalTriggers = consecutiveExternalTriggers,
            LastCompletionTime = LastCompletionTime,
            TotalRounds = loopControlModule.TotalRounds,
            SilentRounds = loopControlModule.SilentRounds,
            AuthorizedToolCount = authorizedTools.Count,
            ParticipantCount = recentParticipants.Count,
            ProcessedMessageCount = processedMessageCount
        };



        // ---- 记忆 ----

        private void TrackMemoryExtraction(
            List<(IncomingMessage Message, SessionContext Context)> messages, SessionContext sc)
        {
            this.lastContext = sc;
            processedMessageCount += messages.Count;
            unrespondedMessageCount += messages.Count;

            if (unrespondedMessageCount >= channelConfig.ExtractionInterval && !lurkingExtractionRunning)
            {
                _ = RunLurkingExtractionAsync(sc);
                unrespondedMessageCount = 0;
            }
        }

        private async Task<List<ScoredMemory>> GetCachedMemoryAsync(SessionContext context, string query)
        {
            int personId = context.Person.Id;

            if (memoryCache.TryGetValue(personId, out var cached) &&
                (DateTime.Now - cached.Time).TotalSeconds < MemoryCacheTtlSeconds)
            {
                FrameworkLogger.Log("ChannelEngine", $"记忆缓存命中: personId={personId}");
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

                FrameworkLogger.Log("ChannelEngine", $"记忆检索超时(15s)，跳过: personId={personId}");
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

                FrameworkLogger.Log("ChannelEngine", "MemoryQueryCore 超时(5s)，跳过意图提取");
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
            await RunLurkingExtractionAsync(lastContext);
        }

        private async Task RunLurkingExtractionAsync(SessionContext context)
        {
            if (lurkingExtractionRunning) return;
            lurkingExtractionRunning = true;
            try
            {
                var limit = Math.Min(channelConfig.ExtractionInterval, 50);
                var recent = await ctx.Session.GetContextByChannelAsync(channelId, limit: limit);
                if (recent.Count < 3) return;

                var lines = recent.Select(m =>
                {
                    var name = m.IsFromBot ? "Lilara"
                             : !string.IsNullOrEmpty(m.SenderName) ? m.SenderName
                             : "用户";
                    return $"{name}: {m.Content}";
                }).ToList();

                var participantNames = recentParticipants.Values
                    .Select(p => p.DisplayName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct().ToList();
                if (participantNames.Count > 0)
                    lines.Insert(0, $"[群聊参与者: {string.Join("、", participantNames)}]");

                var core = new MemoryExtractionCore();
                var results = await core.ExtractAsync(lines);

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

                if (count > 0)
                    FrameworkLogger.Log("ChannelEngine",
                        $"潜水提取完成: channelId={channelId}, 提取{count}条");
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("ChannelEngine", $"潜水提取失败: {ex.Message}");
            }
            finally
            {
                lurkingExtractionRunning = false;
            }
        }

        private async Task ExtractMemoryAsync(SessionContext context)
        {
            try
            {
                var recent = await ctx.Session.GetContextByChannelAsync(channelId, limit: 10);
                if (recent.Count < 2) return;

                var lines = recent.Select(m =>
                {
                    var name = m.IsFromBot ? "Lilara"
                             : !string.IsNullOrEmpty(m.SenderName) ? m.SenderName
                             : "用户";
                    return $"{name}: {m.Content}";
                }).ToList();

                var participantNames = recentParticipants.Values
                    .Select(p => p.DisplayName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct().ToList();
                if (participantNames.Count > 0)
                    lines.Insert(0, $"[群聊参与者: {string.Join("、", participantNames)}]");

                var core = new MemoryExtractionCore();
                var results = await core.ExtractAsync(lines);

                int factCount = 0, feedbackCount = 0, knowledgeCount = 0;
                foreach (var item in results)
                {
                    if (item.Type == "knowledge")
                    {
                        await ctx.MemorySvc.StoreAsync(item.Content,
                            personId: null, channelId: null,
                            confidence: item.Confidence,
                            type: MemoryType.Knowledge, subject: item.Subject);
                        knowledgeCount++;
                    }
                    else if (item.Type == "feedback" && item.Sentiment != null)
                    {
                        int personId = ResolveAboutToPersonId(item.About) ?? context.Person.Id;
                        await ctx.MemorySvc.ApplyFeedbackAsync(
                            personId, item.Content, item.Sentiment, item.Correction);
                        feedbackCount++;
                    }
                    else
                    {
                        int personId = ResolveAboutToPersonId(item.About) ?? context.Person.Id;
                        string memType = item.Type ?? MemoryType.Fact;
                        await ctx.MemorySvc.StoreAsync(item.Content,
                            personId, context.Channel.Id,
                            confidence: item.Confidence,
                            type: memType, subject: item.Subject);
                        factCount++;
                    }
                }

                if (factCount + feedbackCount + knowledgeCount > 0)
                    FrameworkLogger.Log("ChannelEngine",
                        $"记忆提取: channelId={channelId}, 事实{factCount}条, 反馈{feedbackCount}条, 知识{knowledgeCount}条");
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("ChannelEngine", $"记忆提取失败: {ex.Message}");
            }
        }

        private int? ResolveAboutToPersonId(string? aboutName)
        {
            if (string.IsNullOrEmpty(aboutName)) return null;

            foreach (var (_, info) in recentParticipants)
                if (info.DisplayName.Equals(aboutName, StringComparison.OrdinalIgnoreCase))
                    return info.PersonId;

            foreach (var (_, info) in recentParticipants)
                if (info.DisplayName.Contains(aboutName, StringComparison.OrdinalIgnoreCase)
                    || aboutName.Contains(info.DisplayName, StringComparison.OrdinalIgnoreCase))
                    return info.PersonId;

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
                var sentId = await ctx.Adapters.SendMessageAsync(lastMsg.Platform, new OutgoingMessage
                {
                    ChannelId = lastMsg.ChannelId,
                    Content = content,
                    ReplyTo = i == 0 ? firstReplyTo : null,
                    Mentions = mentions
                });
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
                    pendingImagePaths.Add(a.LocalPath!);
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
            FrameworkLogger.Log("ChannelEngine",
                $"关注规则已更新: channelId={channelId}, 规则数={rules.Count}");
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

                FrameworkLogger.Log("ChannelEngine",
                    $"关注规则命中: channelId={channelId}, ruleId={rule.RuleId}, pattern={rule.Pattern}");

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
