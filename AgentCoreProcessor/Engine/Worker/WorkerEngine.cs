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
    /// 工作引擎。长生命周期，一个活跃频道一个实例。
    /// 负责消息缓冲聚合、冲动值决策、参与者追踪、消息处理（分类→记忆→回复→提取）。
    /// </summary>
    internal class WorkerEngine : ISubEngine
    {
        public string EngineType => "Worker";
        public bool IsAlive { get; private set; } = true;

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
        private long _busyFlag = 0;
        private long _completionTicks = 0;

        // ---- 消息缓冲 ----
        private readonly object bufferLock = new();
        private readonly List<(IncomingMessage Message, SessionContext Context)> buffer = new();
        private DateTime lastBufferTime;


        // ---- 冲动值 ----
        private readonly ImpulseConfig impulseConfig;
        private float impulse = 0f;
        private DateTime lastImpulseDecay;
        private readonly float channelAffinity;

        // EMA 社交满足度
        private float expectation = 0f;
        private float reality = 0f;
        private DateTime lastEmaDecay;

        // 消息频率 EMA
        private float messageRate = 0f;
        private DateTime lastMessageRateUpdate;

        // 参与者追踪
        private readonly ConcurrentDictionary<int, ParticipantInfo> recentParticipants = new();

        // Core 实例
        private readonly AgentCore agentCore = new();
        private readonly PreprocessingCore preprocessingCore;
        private readonly PromptBuilder promptBuilder = new();

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

        // 记忆提取计数
        private int processedMessageCount = 0;
        private const int MemoryExtractionInterval = 3;
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


        /// <summary>由 SpawnCheck 创建，传入初始消息。</summary>
        public WorkerEngine(ISystemContext ctx, SessionContext initialContext, IncomingMessage initialMessage)
        {
            this.ctx = ctx;
            this.impulseConfig = ctx.ImpulseConfig;
            this.channelId = initialContext.Channel.Id;
            this.channelAffinity = initialContext.Channel.Affinity;
            var now = DateTime.Now;
            this.lastImpulseDecay = now;
            this.lastEmaDecay = now;
            this.lastMessageRateUpdate = now;
            this.lastBufferTime = now;
            this.preprocessingCore = new PreprocessingCore(ctx.Embedding);

            buffer.Add((initialMessage, initialContext));
            CollectImagePaths(initialMessage);
            recentParticipants.TryAdd(initialContext.User.Id, ParticipantInfo.From(initialContext.User, initialContext.Person, initialMessage));
            AccumulateImpulse(initialMessage, initialContext);
            InitModules();
            ScheduleBufferSignal();

            FrameworkLogger.Log("WorkerEngine", $"创建: channelId={channelId}, affinity={channelAffinity:F2}");
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
            AccumulateImpulse(msg, sc);
            ScheduleBufferSignal();
        }

        /// <summary>缓冲窗口到期后 Signal 闸门。每次新消息重置定时器。</summary>
        private void ScheduleBufferSignal()
        {
            _bufferTimerCts?.Cancel();
            _bufferTimerCts = new CancellationTokenSource();
            var cts = _bufferTimerCts;
            _ = Task.Delay(TimeSpan.FromSeconds(impulseConfig.BufferWindowSeconds), cts.Token)
                .ContinueWith(_ => gate.Signal(), TaskContinuationOptions.NotOnCanceled);
        }


        public async Task RunAsync()
        {
            FrameworkLogger.Log("WorkerEngine", $"启动: channelId={channelId}");
            WireModuleCallbacks();

            while (IsAlive)
            {
                // ① WaitGate
                var triggered = await gate.WaitAsync(
                    TimeSpan.FromSeconds(impulseConfig.ColdTimeoutSeconds));

                if (!triggered)
                {
                    if (processedMessageCount > 0 && lastContext != null)
                        await ExtractMemoryAsync(lastContext);
                    FrameworkLogger.Log("WorkerEngine", $"冷却退出: channelId={channelId}");
                    IsAlive = false;
                    break;
                }

                DecayImpulse();

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
                    FrameworkLogger.LogError("WorkerEngine", ex, $"channelId={channelId}");
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
                await ctx.MemorySvc.StoreAsync(content, currentLastSc.Person.Id, currentLastSc.Channel.Id);
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

                FrameworkLogger.Log("WorkerEngine",
                    $"处理批次: channelId={channelId}, 消息数={batch.Count}, " +
                    $"user={currentLastSc.User.PlatformId} person={currentLastSc.Person.Id}");

                if (ctx.MuteMode)
                {
                    TrackMemoryExtraction(batch, currentLastSc);
                    return false;
                }
                if (!ShouldRespond(batch)) return false;

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
                    FrameworkLogger.Log("WorkerEngine", $"分类结果: {(isTask ? "任务" : "聊天")}");
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

                // 冲动值扣减 + expectation 更新
                bool triggeredByMention = batch.Any(b => b.Message.IsMentioned || b.Message.IsPrivate);
                float dynamicThreshold = impulseConfig.BaseThreshold + messageRate * impulseConfig.MessageRateScaleFactor;
                impulse = Math.Max(0f, impulse - dynamicThreshold);
                if (triggeredByMention)
                    expectation += impulseConfig.ExpectationOnMentionTriggered;
                else
                    expectation += impulseConfig.ExpectationOnProactive;

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
                FrameworkLogger.Log("WorkerEngine",
                    $"Working 续轮: channelId={channelId}, round={loopControlModule.TotalRounds}");
            }

            // 每轮统一重建上下文
            if (currentLastMsg == null || currentLastSc == null) return false;
            currentParticipantSnapshot ??= new Dictionary<int, ParticipantInfo>(recentParticipants);

            var recentMessages = await ctx.Session.GetContextByChannelAsync(channelId);
            var effectiveBatch = batch ?? new List<(IncomingMessage, SessionContext)>();
            var (xml, quotedImagePaths) = await BuildContextXmlAsync(
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
            var toolDescs = isWorkingMode
                ? ToolRegistry.GenerateDescriptions(authorizedTools: authorizedTools)
                : ToolRegistry.GenerateCapabilitySummary();

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
                    FrameworkLogger.Log("WorkerEngine", $"Express 触发报警: channelId={channelId}");
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
                    FrameworkLogger.Log("WorkerEngine", $"Working idle: channelId={channelId}");
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

                FrameworkLogger.Log("WorkerEngine",
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
                    FrameworkLogger.Log("WorkerEngine", $"Express→Working 升级: channelId={channelId}");
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
                    FrameworkLogger.Log("WorkerEngine",
                        $"ContinueLoop 自唤醒: channelId={channelId}, round={loopControlModule.TotalRounds}");
                    gate.Signal();
                }
                else
                {
                    if (loopControlModule.IsMaxSilentReached && speakModule.OnSpeak != null)
                        speakModule.OnSpeak("（工作暂停，等待回应后继续）").GetAwaiter().GetResult();

                    FrameworkLogger.Log("WorkerEngine",
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
                FrameworkLogger.Log("WorkerEngine",
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


        // ---- 冲动值决策 ----

        private bool ShouldRespond(List<(IncomingMessage Message, SessionContext Context)> batch)
        {
            if (batch.Any(b => b.Message.IsPrivate))
            {
                FrameworkLogger.Log("WorkerEngine", $"决策: 私聊必回, channelId={channelId}");
                return true;
            }
            if (batch.Any(b => b.Message.IsMentioned))
            {
                FrameworkLogger.Log("WorkerEngine", $"决策: @提及必回, channelId={channelId}");
                return true;
            }

            if (LastCompletionTime != null &&
                (DateTime.Now - LastCompletionTime.Value).TotalSeconds < impulseConfig.PostResponseCooldownSeconds)
            {
                FrameworkLogger.Log("WorkerEngine",
                    $"决策: 发言冷却中, channelId={channelId}, " +
                    $"elapsed={(DateTime.Now - LastCompletionTime.Value).TotalSeconds:F1}s");
                return false;
            }

            float dynamicThreshold = impulseConfig.BaseThreshold
                + messageRate * impulseConfig.MessageRateScaleFactor;
            bool respond = impulse >= dynamicThreshold;
            FrameworkLogger.Log("WorkerEngine",
                $"决策: impulse={impulse:F2}, threshold={dynamicThreshold:F1}" +
                $"(base={impulseConfig.BaseThreshold}+rate={messageRate:F2}x{impulseConfig.MessageRateScaleFactor}), " +
                $"ratio={ComputeRatioFactor():F2}(E={expectation:F2}/R={reality:F2}), " +
                $"respond={respond}, channelId={channelId}");
            return respond;
        }

        private void AccumulateImpulse(IncomingMessage msg, SessionContext? sc = null)
        {
            var cfg = impulseConfig;
            float participantFactor = recentParticipants.Count switch
            {
                <= 1 => 1.0f,
                2 => 0.9f,
                3 => 0.8f,
                _ => 0.6f
            };
            float ratioFactor = ComputeRatioFactor();
            float added = cfg.BaseMessageScore * channelAffinity * participantFactor * ratioFactor;
            if (msg.IsMentioned) added += cfg.MentionScore;
            if (msg.IsPrivate) added += cfg.PrivateScore;
            impulse += added;

            // 更新消息频率 EMA
            var now = DateTime.Now;
            var elapsed = (float)(now - lastMessageRateUpdate).TotalSeconds;
            if (elapsed > 0)
            {
                float instantRate = 1f / Math.Max(elapsed, 0.1f);
                messageRate = cfg.MessageRateEmaAlpha * instantRate
                    + (1 - cfg.MessageRateEmaAlpha) * messageRate;
                lastMessageRateUpdate = now;
            }

            // 被 @ / 引用 → 更新 reality
            if (sc != null && msg.IsMentioned)
            {
                float trustMult = cfg.GetTrustMultiplier(sc.Person.TrustLevel);
                reality += cfg.RealityOnEngagement * trustMult;
            }

            FrameworkLogger.Log("WorkerEngine",
                $"冲动值+{added:F2}: impulse={impulse:F2}, ratio={ratioFactor:F2}, " +
                $"affinity={channelAffinity:F2}, participants={recentParticipants.Count}, " +
                $"msgRate={messageRate:F2}, mentioned={msg.IsMentioned}, channelId={channelId}");
        }

        private void DecayImpulse()
        {
            var now = DateTime.Now;
            var elapsed = (float)(now - lastImpulseDecay).TotalSeconds;
            lastImpulseDecay = now;
            impulse = Math.Max(0f, impulse - impulseConfig.DecayPerSecond * elapsed);

            // EMA 衰减
            var emaElapsed = (float)(now - lastEmaDecay).TotalSeconds;
            lastEmaDecay = now;
            float decayFactor = MathF.Pow(impulseConfig.EmaDecayRate, emaElapsed);
            expectation *= decayFactor;
            reality *= decayFactor;
        }

        private float ComputeRatioFactor()
        {
            float effectiveExpectation = Math.Max(expectation, impulseConfig.BaseExpectation);
            float ratio = reality / effectiveExpectation;
            return Math.Clamp(ratio, impulseConfig.RatioFactorLower, impulseConfig.RatioFactorUpper);
        }


        // ---- 记忆 ----

        private void TrackMemoryExtraction(
            List<(IncomingMessage Message, SessionContext Context)> messages, SessionContext sc)
        {
            this.lastContext = sc;
            processedMessageCount += messages.Count;
            if (processedMessageCount >= MemoryExtractionInterval)
            {
                processedMessageCount = 0;
                _ = ExtractMemoryAsync(sc);
            }
        }

        private async Task<List<ScoredMemory>> GetCachedMemoryAsync(SessionContext context, string query)
        {
            int personId = context.Person.Id;

            if (memoryCache.TryGetValue(personId, out var cached) &&
                (DateTime.Now - cached.Time).TotalSeconds < MemoryCacheTtlSeconds)
            {
                FrameworkLogger.Log("WorkerEngine", $"记忆缓存命中: personId={personId}");
                return cached.Results;
            }

            try
            {
                var results = await ctx.MemorySvc.RecallAsync(
                    personId, context.Channel.Id,
                    query, topK: 10, includeLinks: true, includePersona: true);
                memoryCache[personId] = (results, DateTime.Now);
                return results;
            }
            catch
            {
                return new List<ScoredMemory>();
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

                int factCount = 0, feedbackCount = 0;
                foreach (var item in results)
                {
                    int personId = ResolveAboutToPersonId(item.About) ?? context.Person.Id;

                    if (item.Type == "feedback" && item.Sentiment != null)
                    {
                        await ctx.MemorySvc.ApplyFeedbackAsync(
                            personId, item.Content, item.Sentiment, item.Correction);
                        feedbackCount++;
                    }
                    else
                    {
                        await ctx.MemorySvc.StoreAsync(item.Content,
                            personId, context.Channel.Id,
                            confidence: item.Confidence);
                        factCount++;
                    }
                }

                if (factCount + feedbackCount > 0)
                    FrameworkLogger.Log("WorkerEngine",
                        $"记忆提取: channelId={channelId}, 事实{factCount}条, 反馈{feedbackCount}条");
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("WorkerEngine", $"记忆提取失败: {ex.Message}");
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

        // ---- XML 格式构建 ----

        private const int MaxQuoteDepth = 2;

        private async Task<(string Xml, List<string> QuotedImagePaths)> BuildContextXmlAsync(
            List<(IncomingMessage Message, SessionContext Context)> batch,
            List<UserMessage> recentMessages,
            Dictionary<int, ParticipantInfo> participants)
        {
            var sb = new StringBuilder();
            var quotedImagePaths = new List<string>();
            var shortNames = ResolveShortNames(participants);

            sb.AppendLine("<participants>");
            foreach (var (userId, info) in participants)
            {
                var name = SanitizeAttr(shortNames.GetValueOrDefault(userId, info.DisplayName));
                var nick = SanitizeAttr(info.Nickname);
                var memo = SanitizeAttr(string.IsNullOrEmpty(info.Memo) ? "还不太了解" : info.Memo);
                var relation = TrustLevelToRelation(info.TrustLevel);
                sb.AppendLine($"  <user name=\"{name}\" nickname=\"{nick}\" qq=\"{info.PlatformId}\" relation=\"{relation}\" memo=\"{memo}\"/>");
            }
            sb.AppendLine("</participants>");

            var batchTicks = new HashSet<long>(batch.Select(b => b.Message.Time.Ticks));

            int lastBotIndex = -1;
            for (int i = recentMessages.Count - 1; i >= 0; i--)
            {
                if (recentMessages[i].IsFromBot) { lastBotIndex = i; break; }
            }

            var unrespondedMessages = new List<UserMessage>();
            var historyMessages = new List<UserMessage>();
            for (int i = 0; i < recentMessages.Count; i++)
            {
                if (batchTicks.Contains(recentMessages[i].Time.Ticks)) continue;
                if (i > lastBotIndex && lastBotIndex >= 0 && !recentMessages[i].IsFromBot)
                    unrespondedMessages.Add(recentMessages[i]);
                else if (lastBotIndex < 0 && !recentMessages[i].IsFromBot)
                    unrespondedMessages.Add(recentMessages[i]);
                else
                    historyMessages.Add(recentMessages[i]);
            }

            // 收集上下文中所有可见的 PlatformMessageId
            var contextIds = new HashSet<string>();
            foreach (var m in recentMessages)
                if (!string.IsNullOrEmpty(m.PlatformMessageId)) contextIds.Add(m.PlatformMessageId);
            foreach (var (msg, _) in batch)
                if (!string.IsNullOrEmpty(msg.PlatformMessageId)) contextIds.Add(msg.PlatformMessageId);

            // 收集需要展开的引用目标（不在上下文中的）
            var missingTargets = new HashSet<string>();
            foreach (var m in historyMessages.Concat(unrespondedMessages))
                if (!string.IsNullOrEmpty(m.ReplyToPlatformMessageId) && !contextIds.Contains(m.ReplyToPlatformMessageId))
                    missingTargets.Add(m.ReplyToPlatformMessageId);
            foreach (var (msg, _) in batch)
                if (!string.IsNullOrEmpty(msg.ReplyTo) && !contextIds.Contains(msg.ReplyTo))
                    missingTargets.Add(msg.ReplyTo);

            // 引用上下文递归展开
            if (missingTargets.Count > 0)
                await AppendQuotedContextAsync(sb, missingTargets, contextIds, shortNames, MaxQuoteDepth, quotedImagePaths);

            // history
            if (historyMessages.Count > 0)
            {
                sb.AppendLine("<history>");
                foreach (var m in historyMessages)
                    sb.AppendLine(FormatDbMessage(m, shortNames, contextIds));
                sb.AppendLine("</history>");
            }

            // new
            sb.AppendLine("<new>");
            foreach (var m in unrespondedMessages)
                sb.AppendLine(FormatDbMessage(m, shortNames, contextIds));
            foreach (var (msg, sc) in batch)
                sb.AppendLine(FormatBatchMessage(msg, sc, shortNames, contextIds));
            sb.Append("</new>");

            return (sb.ToString(), quotedImagePaths);
        }

        private string FormatDbMessage(UserMessage m, Dictionary<int, string> shortNames, HashSet<string> contextIds)
        {
            var name = m.IsFromBot ? "Lilara" : ResolveHistoryShortName(m, shortNames);
            var attrs = new StringBuilder();
            if (!string.IsNullOrEmpty(m.PlatformMessageId))
                attrs.Append($" id=\"{SanitizeAttr(m.PlatformMessageId)}\"");
            attrs.Append($" user=\"{SanitizeAttr(name)}\"");
            if (!string.IsNullOrEmpty(m.ReplyToPlatformMessageId))
                attrs.Append($" reply=\"{SanitizeAttr(m.ReplyToPlatformMessageId)}\"");
            if (m.ImageCount > 0)
                attrs.Append($" images=\"{m.ImageCount}\"");
            return $"<msg{attrs}>{SanitizeContent(m.Content)}</msg>";
        }

        private string FormatBatchMessage(IncomingMessage msg, SessionContext sc,
            Dictionary<int, string> shortNames, HashSet<string> contextIds)
        {
            var name = shortNames.GetValueOrDefault(sc.User.Id, sc.User.DisplayName);
            if (string.IsNullOrEmpty(name)) name = msg.DisplayName ?? msg.PlatformUserId;
            var attrs = new StringBuilder();
            if (!string.IsNullOrEmpty(msg.PlatformMessageId))
                attrs.Append($" id=\"{SanitizeAttr(msg.PlatformMessageId)}\"");
            attrs.Append($" user=\"{SanitizeAttr(name)}\"");
            if (!string.IsNullOrEmpty(msg.ReplyTo))
                attrs.Append($" reply=\"{SanitizeAttr(msg.ReplyTo)}\"");
            if (msg.IsMentioned)
                attrs.Append(" mentioned=\"true\"");
            var imgCount = msg.Attachments?.Count(a => a.Type == AttachmentType.Image) ?? 0;
            if (imgCount > 0)
                attrs.Append($" images=\"{imgCount}\"");
            return $"<msg{attrs}>{SanitizeContent(msg.Content)}</msg>";
        }

        private async Task AppendQuotedContextAsync(StringBuilder sb, HashSet<string> targetIds,
            HashSet<string> contextIds, Dictionary<int, string> shortNames, int maxDepth,
            List<string> quotedImagePaths)
        {
            if (targetIds.Count == 0 || maxDepth <= 0) return;

            var expanded = new List<UserMessage>();
            var nextTargets = new HashSet<string>();

            foreach (var targetId in targetIds)
            {
                if (contextIds.Contains(targetId)) continue;
                try
                {
                    var quoted = await ctx.Session.GetByPlatformMessageIdAsync(channelId, targetId);
                    if (quoted != null)
                    {
                        var around = await ctx.Session.GetContextAroundAsync(quoted.Id, channelId, 3);
                        foreach (var m in around)
                        {
                            if (!contextIds.Contains(m.PlatformMessageId ?? ""))
                            {
                                expanded.Add(m);
                                if (!string.IsNullOrEmpty(m.PlatformMessageId))
                                    contextIds.Add(m.PlatformMessageId);
                            }
                        }
                        // 被引用消息自身也有引用？下一层递归
                        if (!string.IsNullOrEmpty(quoted.ReplyToPlatformMessageId)
                            && !contextIds.Contains(quoted.ReplyToPlatformMessageId))
                            nextTargets.Add(quoted.ReplyToPlatformMessageId);
                    }
                }
                catch (Exception ex)
                {
                    FrameworkLogger.Log("WorkerEngine", $"引用上下文查询失败: {ex.Message}");
                }
            }

            // 递归展开下一层
            if (nextTargets.Count > 0 && maxDepth > 1)
                await AppendQuotedContextAsync(sb, nextTargets, contextIds, shortNames, maxDepth - 1, quotedImagePaths);

            if (expanded.Count > 0)
            {
                // 收集引用消息中的图片
                foreach (var m in expanded)
                {
                    if (!string.IsNullOrEmpty(m.ImageHashes))
                    {
                        var paths = await ImageStorage.ResolvePathsAsync(m.ImageHashes);
                        quotedImagePaths.AddRange(paths);
                    }
                }

                sb.AppendLine("<quoted-context>");
                foreach (var m in expanded)
                {
                    var isTarget = targetIds.Contains(m.PlatformMessageId ?? "");
                    var quotedAttr = isTarget ? " quoted=\"true\"" : "";
                    var name = m.IsFromBot ? "Lilara" : ResolveHistoryShortName(m, shortNames);
                    var attrs = new StringBuilder();
                    if (!string.IsNullOrEmpty(m.PlatformMessageId))
                        attrs.Append($" id=\"{SanitizeAttr(m.PlatformMessageId)}\"");
                    attrs.Append($" user=\"{SanitizeAttr(name)}\"");
                    attrs.Append(quotedAttr);
                    if (!string.IsNullOrEmpty(m.ReplyToPlatformMessageId))
                        attrs.Append($" reply=\"{SanitizeAttr(m.ReplyToPlatformMessageId)}\"");
                    if (m.ImageCount > 0)
                        attrs.Append($" images=\"{m.ImageCount}\"");
                    sb.AppendLine($"<msg{attrs}>{SanitizeContent(m.Content)}</msg>");
                }
                sb.AppendLine("</quoted-context>");
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

        private static string TrustLevelToRelation(TrustLevel level) => level switch
        {
            TrustLevel.Hostile => "不太想理",
            TrustLevel.Wary => "有点警惕",
            TrustLevel.Unknown => "陌生人",
            TrustLevel.Stranger => "不太熟",
            TrustLevel.Understanding => "认识",
            TrustLevel.Familiarity => "熟人",
            TrustLevel.Trust => "好友",
            TrustLevel.AbsoluteTrust => "挚友",
            _ => "陌生人"
        };

        private static Dictionary<int, string> ResolveShortNames(Dictionary<int, ParticipantInfo> participants)
        {
            var result = new Dictionary<int, string>();
            var groups = participants.GroupBy(p => p.Value.DisplayName, StringComparer.Ordinal);

            foreach (var group in groups)
            {
                var members = group.ToList();
                if (members.Count == 1)
                {
                    result[members[0].Key] = members[0].Value.DisplayName;
                }
                else
                {
                    var nicknames = members.Select(m => m.Value.Nickname).ToList();
                    bool nicknamesUnique = nicknames.Distinct().Count() == nicknames.Count
                                           && nicknames.All(n => !string.IsNullOrEmpty(n));
                    foreach (var member in members)
                    {
                        if (nicknamesUnique && !string.IsNullOrEmpty(member.Value.Nickname))
                            result[member.Key] = $"{member.Value.DisplayName}({member.Value.Nickname})";
                        else
                        {
                            var pid = member.Value.PlatformId;
                            var suffix = pid.Length > 4 ? pid[^4..] : pid;
                            result[member.Key] = $"{member.Value.DisplayName}(…{suffix})";
                        }
                    }
                }
            }
            return result;
        }

        private static string ResolveHistoryShortName(UserMessage m, Dictionary<int, string> shortNames)
        {
            if (shortNames.TryGetValue(m.UserId, out var name))
                return name;
            return !string.IsNullOrEmpty(m.SenderName) ? m.SenderName : "用户";
        }

        private static string SanitizeAttr(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var s = value.Replace("\n", " ").Replace("\r", "").Replace("\"", "'");
            s = s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            return s.Length > 40 ? s[..40] : s;
        }

        private static string SanitizeContent(string? content)
        {
            if (string.IsNullOrEmpty(content)) return "";
            return content.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
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
    }
}
