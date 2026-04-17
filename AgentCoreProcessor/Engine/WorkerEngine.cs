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
        private float impulse = 0f;
        private DateTime lastImpulseDecay;
        private readonly float channelAffinity;

        // 参与者追踪
        private readonly ConcurrentDictionary<int, ParticipantInfo> recentParticipants = new();

        // 负反馈抑制
        private bool awaitingResponse = false;
        private int consecutiveIgnores = 0;

        // 缓冲/冲动值常量
        private const float BufferWindowSeconds = 2.5f;
        private const float ColdTimeoutSeconds = 600f;
        private const float MentionScore = 8f;
        private const float BaseMessageScore = 1f;
        private const float PrivateScore = 8f;
        private const float DecayPerSecond = 0.5f;
        private const float ResponseThreshold = 3f;
        private const float PostResponseCooldownSeconds = 3f;
        private const float IgnoreThresholdBoost = 1.5f;
        private const int MaxIgnoreBoost = 3;

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

        // 任务路径消息通道
        private ConcurrentQueue<IncomingMessage>? activeMessageQueue;
        private SemaphoreSlim? activeMessageSignal;

        // 未消费的图片路径（跨 batch 保留，直到 ProcessBatch 消费）
        private readonly List<string> pendingImagePaths = new();
// PLACEHOLDER_CTOR

        /// <summary>由 SpawnCheck 创建，传入初始消息。</summary>
        public WorkerEngine(ISystemContext ctx, SessionContext initialContext, IncomingMessage initialMessage)
        {
            this.ctx = ctx;
            this.channelId = initialContext.Channel.Id;
            this.channelAffinity = initialContext.Channel.Affinity;
            this.lastImpulseDecay = DateTime.Now;
            this.lastBufferTime = DateTime.Now;
            this.preprocessingCore = new PreprocessingCore(ctx.Embedding);

            buffer.Add((initialMessage, initialContext));
            CollectImagePaths(initialMessage);
            recentParticipants.TryAdd(initialContext.User.Id, ParticipantInfo.From(initialContext.User, initialMessage));
            AccumulateImpulse(initialMessage);

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
                ParticipantInfo.From(sc.User, msg),
                (_, _) => ParticipantInfo.From(sc.User, msg));
            AccumulateImpulse(msg);
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
                        (DateTime.Now - lastBufferTime).TotalSeconds >= BufferWindowSeconds)
                    {
                        batch = new(buffer);
                        buffer.Clear();
                    }
                }

                if (batch != null)
                {
                    FrameworkLogger.Log("WorkerEngine",
                        $"缓冲触发: channelId={channelId}, 消息数={batch.Count}, impulse={impulse:F2}");

                    // 负反馈检查
                    if (awaitingResponse)
                    {
                        bool gotResponse = batch.Any(b => b.Message.IsMentioned);
                        if (gotResponse)
                            consecutiveIgnores = 0;
                        else
                            consecutiveIgnores = Math.Min(consecutiveIgnores + 1, MaxIgnoreBoost);
                        awaitingResponse = false;
                        FrameworkLogger.Log("WorkerEngine",
                            $"负反馈: gotResponse={gotResponse}, consecutiveIgnores={consecutiveIgnores}");
                    }

                    if (!ctx.MuteMode && ShouldRespond(batch))
                    {
                        bool triggeredByMention = batch.Any(b => b.Message.IsMentioned || b.Message.IsPrivate);
                        Interlocked.Exchange(ref _busyFlag, 1);
                        try
                        {
                            var snapshot = new Dictionary<int, ParticipantInfo>(recentParticipants);
                            await ProcessBatchAsync(batch, snapshot);
                            impulse = 0f;
                            // 只有主动发言（非@/非私聊触发）才等待对方回应
                            if (!triggeredByMention)
                                awaitingResponse = true;
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
                    (DateTime.Now - lastBufferTime).TotalSeconds > ColdTimeoutSeconds)
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
                (DateTime.Now - LastCompletionTime.Value).TotalSeconds < PostResponseCooldownSeconds)
            {
                FrameworkLogger.Log("WorkerEngine",
                    $"决策: 发言冷却中, channelId={channelId}, " +
                    $"elapsed={(DateTime.Now - LastCompletionTime.Value).TotalSeconds:F1}s");
                return false;
            }

            float effectiveThreshold = ResponseThreshold + consecutiveIgnores * IgnoreThresholdBoost;
            bool respond = impulse >= effectiveThreshold;
            FrameworkLogger.Log("WorkerEngine",
                $"决策: impulse={impulse:F2}, threshold={effectiveThreshold:F1}" +
                $"(base={ResponseThreshold}+ignore={consecutiveIgnores}x{IgnoreThresholdBoost}), " +
                $"respond={respond}, channelId={channelId}");
            return respond;
        }

        private void AccumulateImpulse(IncomingMessage msg)
        {
            float participantFactor = recentParticipants.Count switch
            {
                <= 1 => 1.0f,
                2 => 0.9f,
                3 => 0.8f,
                _ => 0.6f
            };
            float added = BaseMessageScore * channelAffinity * participantFactor;
            if (msg.IsMentioned) added += MentionScore;
            if (msg.IsPrivate) added += PrivateScore;
            impulse += added;
            FrameworkLogger.Log("WorkerEngine",
                $"冲动值+{added:F2}: impulse={impulse:F2}, " +
                $"affinity={channelAffinity:F2}, participants={recentParticipants.Count}, " +
                $"mentioned={msg.IsMentioned}, private={msg.IsPrivate}, channelId={channelId}");
        }

        private void DecayImpulse()
        {
            var now = DateTime.Now;
            var elapsed = (float)(now - lastImpulseDecay).TotalSeconds;
            lastImpulseDecay = now;
            impulse = Math.Max(0f, impulse - DecayPerSecond * elapsed);
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
            var formattedContext = await BuildContextXmlAsync(messages, lastSc.RecentMessages, participantSnapshot);

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

            // 4. 分类
            var isTask = await preprocessingCore.IsTaskAsync(formattedContext);
            FrameworkLogger.Log("WorkerEngine", $"分类结果: {(isTask ? "任务" : "聊天")}");

            // 5. 查记忆
            var memoryResults = await GetCachedMemoryAsync(lastSc, lastMsg.Content);
// PLACEHOLDER_ROUTE

            // 6. 路由处理：先尝试聊天，ExpressCore 可转交任务
            if (!isTask)
            {
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

                expressCore.ResetProcessor();
                var expressInput = inputBuilder.ToString();
                var expressed = imagePaths.Count > 0
                    ? await expressCore.GenerateOnceAsync(expressInput, imagePaths)
                    : await expressCore.GenerateOnceAsync(expressInput);

                if (expressed.Contains("[TASK]"))
                {
                    FrameworkLogger.Log("WorkerEngine", $"ExpressCore 转交任务: channelId={channelId}");
                    // 发送 [TASK] 之前的内容作为快速响应
                    var preTask = expressed.Split("[TASK]")[0].Trim();
                    if (!string.IsNullOrEmpty(preTask))
                    {
                        var sentId = await ctx.Adapters.SendMessageAsync(lastMsg.Platform, new OutgoingMessage
                        {
                            ChannelId = lastMsg.ChannelId,
                            Content = preTask
                        });
                        await ctx.Session.SaveBotMessageAsync(lastSc.Channel.Id, preTask, sentId);
                    }
                    isTask = true;
                }
                else
                {
                    // 按换行拆分为多条消息，逐条发送
                    var segments = expressed
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => s.Length > 0)
                        .ToList();

                if (segments.Count <= 1)
                {
                    // 单条直接发
                    var sentId = await ctx.Adapters.SendMessageAsync(lastMsg.Platform, new OutgoingMessage
                    {
                        ChannelId = lastMsg.ChannelId,
                        Content = expressed.Trim()
                    });
                    await ctx.Session.SaveBotMessageAsync(lastSc.Channel.Id, expressed.Trim(), sentId);
                }
                else
                {
                    var rng = new Random();
                    for (int i = 0; i < segments.Count; i++)
                    {
                        if (i > 0)
                            await Task.Delay(rng.Next(600, 2000));
                        var sentId = await ctx.Adapters.SendMessageAsync(lastMsg.Platform, new OutgoingMessage
                        {
                            ChannelId = lastMsg.ChannelId,
                            Content = segments[i]
                        });
                        await ctx.Session.SaveBotMessageAsync(lastSc.Channel.Id, segments[i], sentId);
                    }
                }
                }
            }

            // 任务路径（直接分类为任务，或 ExpressCore 转交）
            if (isTask)
            {
                var taskMemory = memoryResults?.Where(m => !m.IsPersona).ToList();
                string? memoryContext = FormatMemory(taskMemory, topK: 10);

                workingCore.OnSpeak = async (rawText) =>
                {
                    var sentId = await ctx.Adapters.SendMessageAsync(lastMsg.Platform, new OutgoingMessage
                    {
                        ChannelId = lastMsg.ChannelId,
                        Content = rawText
                    });
                    await ctx.Session.SaveBotMessageAsync(lastSc.Channel.Id, rawText, sentId);
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

                var msgQueue = new ConcurrentQueue<IncomingMessage>();
                var msgSignal = new SemaphoreSlim(0);
                workingCore.SetMessageChannel(msgQueue, msgSignal);
                this.activeMessageQueue = msgQueue;
                this.activeMessageSignal = msgSignal;

                try
                {
                    await workingCore.ProcessAsync(formattedContext, memoryContext,
                        imagePaths: imagePaths.Count > 0 ? imagePaths : null);
                }
                finally
                {
                    this.activeMessageQueue = null;
                    this.activeMessageSignal = null;
                }
            }

            // 7. 记忆提取计数
            TrackMemoryExtraction(messages, lastSc);
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

        // ---- XML 格式构建 ----

        private async Task<string> BuildContextXmlAsync(
            List<(IncomingMessage Message, SessionContext Context)> batch,
            List<UserMessage> recentMessages,
            Dictionary<int, ParticipantInfo> participants)
        {
            var sb = new StringBuilder();
            var shortNames = ResolveShortNames(participants);

            sb.AppendLine("<participants>");
            foreach (var (userId, info) in participants)
            {
                var name = SanitizeAttr(shortNames.GetValueOrDefault(userId, info.DisplayName));
                var nick = SanitizeAttr(info.Nickname);
                sb.AppendLine($"  <user name=\"{name}\" nickname=\"{nick}\" qq=\"{info.PlatformId}\"/>");
            }
            sb.AppendLine("</participants>");

            // 引用上下文补全
            var replyMsg = batch.FirstOrDefault(b => !string.IsNullOrEmpty(b.Message.ReplyTo));
            if (replyMsg.Message != null)
                await AppendQuotedContextAsync(sb, replyMsg.Message, shortNames);

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

            if (historyMessages.Count > 0)
            {
                sb.AppendLine("<history>");
                foreach (var m in historyMessages)
                {
                    var name = m.IsFromBot ? "Lilara" : ResolveHistoryShortName(m, shortNames);
                    var imgAttr = m.ImageCount > 0 ? $" images=\"{m.ImageCount}\"" : "";
                    sb.AppendLine($"<msg user=\"{SanitizeAttr(name)}\"{imgAttr}>{SanitizeContent(m.Content)}</msg>");
                }
                sb.AppendLine("</history>");
            }

            sb.AppendLine("<new>");
            foreach (var m in unrespondedMessages)
            {
                var name = ResolveHistoryShortName(m, shortNames);
                var imgAttr = m.ImageCount > 0 ? $" images=\"{m.ImageCount}\"" : "";
                sb.AppendLine($"<msg user=\"{SanitizeAttr(name)}\"{imgAttr}>{SanitizeContent(m.Content)}</msg>");
            }
            foreach (var (msg, sc) in batch)
            {
                var name = shortNames.GetValueOrDefault(sc.User.Id, sc.User.DisplayName);
                if (string.IsNullOrEmpty(name)) name = msg.DisplayName ?? msg.PlatformUserId;
                var mentionAttr = msg.IsMentioned ? " mentioned=\"true\"" : "";
                var batchImgCount = msg.Attachments?.Count(a => a.Type == AttachmentType.Image) ?? 0;
                var imgAttr = batchImgCount > 0 ? $" images=\"{batchImgCount}\"" : "";
                if (!string.IsNullOrEmpty(msg.QuotedContent) || !string.IsNullOrEmpty(msg.ReplyTo))
                {
                    sb.AppendLine($"<msg user=\"{SanitizeAttr(name)}\"{mentionAttr}{imgAttr}>");
                    if (!string.IsNullOrEmpty(msg.QuotedContent))
                        sb.AppendLine($"  <quote>{SanitizeContent(msg.QuotedContent)}</quote>");
                    sb.AppendLine($"  {SanitizeContent(msg.Content)}");
                    sb.AppendLine("</msg>");
                }
                else
                {
                    sb.AppendLine($"<msg user=\"{SanitizeAttr(name)}\"{mentionAttr}{imgAttr}>{SanitizeContent(msg.Content)}</msg>");
                }
            }
            sb.Append("</new>");

            return sb.ToString();
        }

        private async Task AppendQuotedContextAsync(StringBuilder sb, IncomingMessage replyMsg,
            Dictionary<int, string> shortNames)
        {
            try
            {
                var quoted = await ctx.Session.GetByPlatformMessageIdAsync(channelId, replyMsg.ReplyTo!);
                if (quoted != null)
                {
                    var around = await ctx.Session.GetContextAroundAsync(quoted.Id, channelId, 3);
                    if (around.Count > 0)
                    {
                        sb.AppendLine("<quoted-context>");
                        foreach (var m in around)
                        {
                            var name = m.IsFromBot ? "Lilara" : ResolveHistoryShortName(m, shortNames);
                            var isTarget = m.Id == quoted.Id ? " quoted=\"true\"" : "";
                            var imgAttr = m.ImageCount > 0 ? $" images=\"{m.ImageCount}\"" : "";
                            sb.AppendLine($"<msg user=\"{SanitizeAttr(name)}\"{isTarget}{imgAttr}>{SanitizeContent(m.Content)}</msg>");
                        }
                        sb.AppendLine("</quoted-context>");
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("WorkerEngine", $"引用上下文查询失败: {ex.Message}");
            }

            if (!string.IsNullOrEmpty(replyMsg.QuotedContent))
            {
                sb.AppendLine("<quoted-context>");
                sb.AppendLine($"<msg quoted=\"true\">{SanitizeContent(replyMsg.QuotedContent)}</msg>");
                sb.AppendLine("</quoted-context>");
            }
        }
// PLACEHOLDER_HELPERS

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
