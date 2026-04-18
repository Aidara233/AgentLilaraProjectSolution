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
using AgentCoreProcessor.Memory;
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
// PLACEHOLDER_IMPULSE

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

        // Core 实例（复用）
        private readonly ExpressCore expressCore = new();
        private readonly WorkingCore workingCore = new();
        private readonly PreprocessingCore preprocessingCore;

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

        // 任务路径消息通道
        private ConcurrentQueue<IncomingMessage>? activeMessageQueue;
        private SemaphoreSlim? activeMessageSignal;

        // 未消费的图片路径（跨 batch 保留，直到 ProcessBatch 消费）
        private readonly List<string> pendingImagePaths = new();

        // 便签板（Express/Working 共享，会话级生命周期）
        private readonly Dictionary<string, string> pinboard = new();

        // Express/Working 自适应切换
        private bool isWorkingMode = false;
        private int consecutiveExternalTriggers = 0;
        private const int WorkingToExpressThreshold = 3;
// PLACEHOLDER_CTOR

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

            FrameworkLogger.Log("WorkerEngine", $"创建: channelId={channelId}, affinity={channelAffinity:F2}");
        }

        /// <summary>由 SpawnCheck 调用，将新消息加入缓冲。</summary>
        public void EnqueueMessage(IncomingMessage msg, SessionContext sc)
        {
            // 忙时且有活跃消息通道：转发给 WorkingCore
            if (IsBusy && activeMessageQueue != null)
            {
                activeMessageQueue.Enqueue(msg);
                activeMessageSignal?.Release();
                FrameworkLogger.Log("WorkerEngine",
                    $"忙时消息转发: channelId={channelId}");
                return;
            }

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
        }
// PLACEHOLDER_RUN

        public async Task RunAsync()
        {
            FrameworkLogger.Log("WorkerEngine", $"启动: channelId={channelId}");

            while (IsAlive)
            {
                await Task.Delay(200);

                DecayImpulse();

                // 检查缓冲窗口是否到期
                List<(IncomingMessage Message, SessionContext Context)>? batch = null;
                lock (bufferLock)
                {
                    if (buffer.Count > 0 &&
                        (DateTime.Now - lastBufferTime).TotalSeconds >= impulseConfig.BufferWindowSeconds)
                    {
                        batch = new(buffer);
                        buffer.Clear();
                    }
                }

                if (batch != null)
                {
                    FrameworkLogger.Log("WorkerEngine",
                        $"缓冲触发: channelId={channelId}, 消息数={batch.Count}, impulse={impulse:F2}");

                    if (!ctx.MuteMode && ShouldRespond(batch))
                    {
                        bool triggeredByMention = batch.Any(b => b.Message.IsMentioned || b.Message.IsPrivate);
                        Interlocked.Exchange(ref _busyFlag, 1);
                        try
                        {
                            var snapshot = new Dictionary<int, ParticipantInfo>(recentParticipants);
                            await ProcessBatchAsync(batch, snapshot);
                            // 触发后扣减阈值，不归零
                            float dynamicThreshold = impulseConfig.BaseThreshold
                                + messageRate * impulseConfig.MessageRateScaleFactor;
                            impulse = Math.Max(0f, impulse - dynamicThreshold);
                            // 更新 expectation
                            if (triggeredByMention)
                                expectation += impulseConfig.ExpectationOnMentionTriggered;
                            else
                                expectation += impulseConfig.ExpectationOnProactive;
                        }
                        catch (Exception ex)
                        {
                            var msg = batch[0].Message;
                            FrameworkLogger.LogError("WorkerEngine", ex,
                                $"channelId={channelId} channel={msg.ChannelId}");
                            try
                            {
                                await ctx.Adapters.SendMessageAsync(msg.Platform, new OutgoingMessage
                                {
                                    ChannelId = msg.ChannelId,
                                    Content = $"[错误] 处理消息时发生异常：{ex.Message}"
                                });
                            }
                            catch { }
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _busyFlag, 0);
                            Interlocked.Exchange(ref _completionTicks, DateTime.Now.Ticks);
                        }
                    }

                    // 静音模式下仍追踪记忆提取
                    if (ctx.MuteMode)
                        TrackMemoryExtraction(batch, batch[^1].Context);
                }

                // 冷却检查
                bool bufferEmpty;
                lock (bufferLock) { bufferEmpty = buffer.Count == 0; }
                if (bufferEmpty && impulse <= 0.01f && !IsBusy &&
                    (DateTime.Now - lastBufferTime).TotalSeconds > impulseConfig.ColdTimeoutSeconds)
                {
                    // 退出前：剩余消息强制提取记忆
                    if (processedMessageCount > 0 && lastContext != null)
                        await ExtractMemoryAsync(lastContext);

                    FrameworkLogger.Log("WorkerEngine", $"冷却退出: channelId={channelId}");
                    IsAlive = false;
                }
            }
        }

        public void OnEvent(EngineEvent e) { }

        public void RequestStop()
        {
            IsAlive = false;
        }
// PLACEHOLDER_IMPULSE_METHODS

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
// PLACEHOLDER_PROCESS

        // ---- 批次处理 ----

        private async Task ProcessBatchAsync(
            List<(IncomingMessage Message, SessionContext Context)> messages,
            Dictionary<int, ParticipantInfo> participantSnapshot)
        {
            var lastMsg = messages[^1].Message;
            var lastSc = messages[^1].Context;

            FrameworkLogger.Log("WorkerEngine",
                $"处理批次: channelId={channelId}, 消息数={messages.Count}, " +
                $"user={lastSc.User.PlatformId} person={lastSc.Person.Id}");

            // 1. 消费 pending 图片
            List<string> imagePaths;
            lock (bufferLock)
            {
                imagePaths = new List<string>(pendingImagePaths);
                pendingImagePaths.Clear();
            }

            // 2. 构建 XML 上下文
            var (formattedContext, quotedImagePaths) = await BuildContextXmlAsync(messages, lastSc.RecentMessages, participantSnapshot);
            imagePaths.AddRange(quotedImagePaths);

            if (imagePaths.Count > 0)
            {
                var prefix = imagePaths.Count == 1 ? "（用户发送了一张图片）" : $"（用户发送了{imagePaths.Count}张图片）";
                formattedContext += $"\n\n{prefix}";
            }

            // 3. 标记本批消息为已处理
            foreach (var (msg, _) in messages)
            {
                processedTicks.AddLast(msg.Time.Ticks);
                while (processedTicks.Count > MaxProcessedTicksWindow)
                    processedTicks.RemoveFirst();
            }

            // 4. 分类（Working 模式下跳过，直接走 Working）
            if (!isWorkingMode)
            {
                var isTask = await preprocessingCore.IsTaskAsync(formattedContext);
                FrameworkLogger.Log("WorkerEngine", $"分类结果: {(isTask ? "任务" : "聊天")}");
                if (isTask)
                {
                    isWorkingMode = true;
                    consecutiveExternalTriggers = 0;
                }
            };

            // 5. 查记忆
            var memoryResults = await GetCachedMemoryAsync(lastSc, lastMsg.Content);
// PLACEHOLDER_ROUTE

            // 6. 路由处理：模式状态机
            bool useWorking = isWorkingMode;

            if (!useWorking)
            {
                // Express 模式：聊天，检测 [ESCALATE] 升级
                string? chatMemory = FormatMemory(memoryResults, topK: 5);

                var inputBuilder = new StringBuilder();
                inputBuilder.Append(formattedContext);
                if (chatMemory != null)
                {
                    inputBuilder.AppendLine();
                    inputBuilder.AppendLine();
                    inputBuilder.AppendLine("[记忆参考]");
                    inputBuilder.Append(chatMemory);
                }
                if (pinboard.Count > 0)
                {
                    inputBuilder.AppendLine();
                    inputBuilder.AppendLine();
                    inputBuilder.AppendLine("[便签板]");
                    foreach (var (label, content) in pinboard)
                        inputBuilder.AppendLine($"- {label}: {content}");
                }

                expressCore.ResetProcessor();
                var expressInput = inputBuilder.ToString();
                var expressed = imagePaths.Count > 0
                    ? await expressCore.GenerateOnceAsync(expressInput, imagePaths)
                    : await expressCore.GenerateOnceAsync(expressInput);

                // [ALERT] 检测
                if (expressed.Contains("[ALERT]"))
                {
                    FrameworkLogger.Log("WorkerEngine", $"ExpressCore 触发报警: channelId={channelId}");
                    var reason = expressed.Replace("[ALERT]", "").Trim();
                    await HandleAlertAsync(lastSc.Person, lastSc, string.IsNullOrEmpty(reason) ? "聊天中触发" : reason);
                    expressed = expressed.Replace("[ALERT]", "").Trim();
                }

                if (expressed.Contains("[ESCALATE]"))
                {
                    FrameworkLogger.Log("WorkerEngine", $"ExpressCore 升级到 Working 模式: channelId={channelId}");
                    var preEscalate = expressed.Split("[ESCALATE]")[0].Trim();
                    if (!string.IsNullOrEmpty(preEscalate))
                    {
                        await SendSegmentsAsync(preEscalate, lastMsg, lastSc, participantSnapshot);
                        formattedContext += $"\n\n[已发送的回复]\n你刚才已经对用户说了：「{preEscalate}」\n不要重复相同意思的话，直接处理需要工具的部分。";
                    }

                    isWorkingMode = true;
                    consecutiveExternalTriggers = 0;
                    useWorking = true;
                }
                else
                {
                    await SendSegmentsAsync(expressed, lastMsg, lastSc, participantSnapshot);
                }
            }

            // Working 模式
            if (useWorking)
            {
                consecutiveExternalTriggers++;

                var taskMemory = memoryResults?.Where(m => !m.IsPersona).ToList();
                string? memoryContext = FormatMemory(taskMemory, topK: 10);

                var msgQueue = new ConcurrentQueue<IncomingMessage>();
                var msgSignal = new SemaphoreSlim(0);

                workingCore.OnSpeak = async (rawText) =>
                {
                    var (content, replyTo, mentions) = ParseBotOutput(rawText, participantSnapshot);
                    var sentId = await ctx.Adapters.SendMessageAsync(lastMsg.Platform, new OutgoingMessage
                    {
                        ChannelId = lastMsg.ChannelId,
                        Content = content,
                        ReplyTo = replyTo,
                        Mentions = mentions
                    });
                    await ctx.Session.SaveBotMessageAsync(lastSc.Channel.Id, content, sentId);
                };
                workingCore.OnMemory = async (content) =>
                {
                    await ctx.MemorySvc.StoreAsync(content, lastSc.Person.Id, lastSc.Channel.Id);
                };
                workingCore.OnSignal = async (signalName, payload) =>
                {
                    ctx.EventBus.PublishSignal(signalName, payload);
                    await Task.CompletedTask;
                };
                workingCore.OnReviewHint = async (content) =>
                {
                    await ctx.ReviewHints.CreateAsync(content, lastSc.Person.Id, lastSc.Channel.Id);
                };
                workingCore.OnAlert = async (reason) =>
                {
                    await HandleAlertAsync(lastSc.Person, lastSc, reason);
                };
                workingCore.OnAuthRequired = async (toolName, requiredLevel) =>
                {
                    return await HandleAuthorizationRequestAsync(
                        new List<string> { toolName }, $"使用工具「{toolName}」",
                        lastMsg, msgQueue, msgSignal);
                };

                workingCore.SetMessageChannel(msgQueue, msgSignal);
                workingCore.SetPinboard(pinboard);
                this.activeMessageQueue = msgQueue;
                this.activeMessageSignal = msgSignal;

                try
                {
                    var exitReason = await workingCore.ProcessAsync(formattedContext, memoryContext,
                        imagePaths: imagePaths.Count > 0 ? imagePaths : null);

                    FrameworkLogger.Log("WorkerEngine",
                        $"WorkingCore 退出: reason={exitReason}, channelId={channelId}");
                }
                finally
                {
                    this.activeMessageQueue = null;
                    this.activeMessageSignal = null;
                }

                // 回退检查：连续 N 次外部消息触发都没用工具 → 回退到 Express
                if (consecutiveExternalTriggers >= WorkingToExpressThreshold)
                {
                    FrameworkLogger.Log("WorkerEngine",
                        $"Working→Express 回退: 连续{consecutiveExternalTriggers}次外部触发, channelId={channelId}");
                    isWorkingMode = false;
                    consecutiveExternalTriggers = 0;
                }
            }

            // 7. 记忆提取计数
            TrackMemoryExtraction(messages, lastSc);

            // 8. TrustProgress 每日自动增长
            await IncrementDailyProgressAsync(lastSc.Person);
        }
// PLACEHOLDER_MEMORY

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

                var core = new MemoryExtractionCore();
                var results = await core.ExtractAsync(lines);

                int factCount = 0, feedbackCount = 0;
                foreach (var item in results)
                {
                    if (item.Type == "feedback" && item.Sentiment != null)
                    {
                        await ctx.MemorySvc.ApplyFeedbackAsync(
                            context.Person.Id, item.Content, item.Sentiment, item.Correction);
                        feedbackCount++;
                    }
                    else
                    {
                        await ctx.MemorySvc.StoreAsync(item.Content,
                            context.Person.Id, context.Channel.Id,
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
// PLACEHOLDER_XML

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
// PLACEHOLDER_HELPERS

        private async Task HandleAlertAsync(Person person, SessionContext sc, string reason)
            => await AlertHandler.HandleAsync(person, sc, reason, ctx);

        private async Task<bool> HandleAuthorizationRequestAsync(
            List<string> toolNames, string reason,
            IncomingMessage triggerMsg,
            ConcurrentQueue<IncomingMessage> msgQueue,
            SemaphoreSlim msgSignal)
        {
            var maxRequired = toolNames
                .Select(n => ToolRegistry.Get(n)?.RequiredPermission ?? PermissionLevel.Admin)
                .Max();

            var code = Random.Shared.Next(1000, 9999).ToString();
            var timeoutSeconds = Math.Clamp(30 + toolNames.Count * 5, 30, 120);

            FrameworkLogger.Log("WorkerEngine",
                $"授权请求: tools=[{string.Join(",", toolNames)}], code={code}, " +
                $"requiredLevel={maxRequired}, timeout={timeoutSeconds}s");

            await ctx.Adapters.SendMessageAsync(triggerMsg.Platform, new Adapter.OutgoingMessage
            {
                ChannelId = triggerMsg.ChannelId,
                Content = $"[授权请求] Lilara 申请使用工具：{string.Join("、", toolNames)}\n" +
                          $"理由：{reason}\n" +
                          $"如果同意，请回复验证码：{code}（{timeoutSeconds}秒内有效）"
            });

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            var pendingMessages = new List<IncomingMessage>();

            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    var waitTime = TimeSpan.FromSeconds(Math.Min(5, remaining.TotalSeconds));
                    await msgSignal.WaitAsync(waitTime);

                    while (msgQueue.TryDequeue(out var msg))
                    {
                        if (msg.Content.Trim().Contains(code))
                        {
                            var (sender, _) = await ctx.Session.ResolveUserAsync(msg);
                            if (sender.PermissionLevel >= maxRequired)
                            {
                                FrameworkLogger.Log("WorkerEngine",
                                    $"授权通过: userId={sender.Id}, level={sender.PermissionLevel}");
                                return true;
                            }
                            FrameworkLogger.Log("WorkerEngine",
                                $"授权验证码匹配但权限不足: userId={sender.Id}, " +
                                $"level={sender.PermissionLevel}, required={maxRequired}");
                        }
                        pendingMessages.Add(msg);
                    }
                }

                FrameworkLogger.Log("WorkerEngine", "授权请求超时");
                return false;
            }
            finally
            {
                foreach (var msg in pendingMessages)
                {
                    msgQueue.Enqueue(msg);
                    msgSignal.Release();
                }
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
