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
    /// 工作引擎。常驻，与 TopicEngine 同生命周期。
    /// 空闲时挂起等待激活，被激活后处理消息批次（分类→记忆→回复→提取）。
    /// </summary>
    internal class WorkerEngine : ISubEngine
    {
        public string EngineType => "Worker";
        public bool IsAlive { get; private set; } = true;

        // ---- 公开状态（TopicEngine 读取） ----

        /// <summary>是否正在处理消息。TopicEngine 读取用于冷却判断。</summary>
        public bool IsBusy => Interlocked.Read(ref _busyFlag) == 1;

        /// <summary>上次处理完成的时间。TopicEngine 读取用于冷却期计算。</summary>
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
        private readonly int topicId;
        private long _busyFlag = 0;
        private long _completionTicks = 0;
        private volatile bool stopRequested = false;

        // 激活队列
        private readonly ConcurrentQueue<ActivationBatch> activationQueue = new();
        private readonly SemaphoreSlim activationSignal = new(0);

        // Core 实例（复用，不每次新建）
        private readonly ExpressCore expressCore = new();
        private readonly WorkingCore workingCore = new();
        private readonly PreprocessingCore preprocessingCore;

        // 已处理消息标记（新旧区分）
        private readonly LinkedList<long> processedTicks = new();
        private const int MaxProcessedTicksWindow = 50;

        // 记忆缓存：per-person
        private readonly Dictionary<int, (List<ScoredMemory> Results, DateTime Time)> memoryCache = new();
        private const float MemoryCacheTtlSeconds = 60f;

        // 记忆提取计数
        private int processedMessageCount = 0;
        private const int MemoryExtractionInterval = 3;
        private SessionContext? lastContext;

        // 任务路径消息通道（WorkingCore 运行期间，新消息推入此队列）
        private ConcurrentQueue<IncomingMessage>? activeMessageQueue;
        private SemaphoreSlim? activeMessageSignal;

        public WorkerEngine(ISystemContext ctx, int topicId)
        {
            this.ctx = ctx;
            this.topicId = topicId;
            this.preprocessingCore = new PreprocessingCore(ctx.Embedding);
        }

        /// <summary>由 TopicEngine 调用，将激活批次推入队列并唤醒事件循环。</summary>
        public void Activate(ActivationBatch batch)
        {
            // 忙时：将消息转发给 WorkingCore 的消息通道（如果有）
            if (IsBusy && activeMessageQueue != null)
            {
                foreach (var (msg, _) in batch.Messages)
                {
                    activeMessageQueue.Enqueue(msg);
                }
                activeMessageSignal?.Release();
                FrameworkLogger.Log("WorkerEngine",
                    $"忙时消息转发: topicId={topicId}, 消息数={batch.Messages.Count}");
                return;
            }

            activationQueue.Enqueue(batch);
            activationSignal.Release();
        }

        public async Task RunAsync()
        {
            FrameworkLogger.Log("WorkerEngine", $"常驻启动: topicId={topicId}");

            while (!stopRequested)
            {
                try
                {
                    // 等待激活信号或超时（超时用于检查 stopRequested）
                    await activationSignal.WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (OperationCanceledException) { break; }

                // 排空队列，合并所有待处理批次
                var batches = new List<ActivationBatch>();
                while (activationQueue.TryDequeue(out var batch))
                    batches.Add(batch);

                if (batches.Count == 0) continue;

                Interlocked.Exchange(ref _busyFlag, 1);
                try
                {
                    var merged = MergeBatches(batches);
                    await ProcessBatchAsync(merged);
                }
                catch (Exception ex)
                {
                    var msg = batches[0].Messages[0].Message;
                    FrameworkLogger.LogError("WorkerEngine", ex,
                        $"topicId={topicId} channel={msg.ChannelId}");

                    // 尝试发送错误提示
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

            // 退出前：剩余消息强制提取记忆
            if (processedMessageCount > 0 && lastContext != null)
                await ExtractMemoryAsync(lastContext);

            FrameworkLogger.Log("WorkerEngine", $"常驻退出: topicId={topicId}");
            IsAlive = false;
        }

        public void OnEvent(EngineEvent e) { }

        public void RequestStop()
        {
            stopRequested = true;
            activationSignal.Release(); // 唤醒等待，让循环检查 stopRequested
        }

        // ---- 批次处理 ----

        /// <summary>合并多个激活批次为一个。参与者快照取最新的。</summary>
        private static ActivationBatch MergeBatches(List<ActivationBatch> batches)
        {
            if (batches.Count == 1) return batches[0];

            var allMessages = new List<(IncomingMessage Message, SessionContext Context)>();
            foreach (var b in batches)
                allMessages.AddRange(b.Messages);

            // 参与者快照合并（后来的覆盖先来的）
            var merged = new Dictionary<int, ParticipantInfo>();
            foreach (var b in batches)
                foreach (var kv in b.ParticipantSnapshot)
                    merged[kv.Key] = kv.Value;

            return new ActivationBatch { Messages = allMessages, ParticipantSnapshot = merged };
        }

        private async Task ProcessBatchAsync(ActivationBatch batch)
        {
            var messages = batch.Messages;
            var lastMsg = messages[^1].Message;
            var lastSc = messages[^1].Context;

            FrameworkLogger.Log("WorkerEngine",
                $"处理批次: topicId={topicId}, 消息数={messages.Count}, " +
                $"user={lastSc.User.PlatformId} person={lastSc.Person.Id}");

            // 1. 收集图片
            var imagePaths = messages
                .Where(b => b.Message.Attachments != null)
                .SelectMany(b => b.Message.Attachments!)
                .Where(a => a.Type == AttachmentType.Image && !string.IsNullOrEmpty(a.LocalPath))
                .Select(a => a.LocalPath!)
                .ToList();

            // 2. 构建 XML 上下文（参与者 + 历史 + 新消息）
            var formattedContext = BuildContextXml(messages, lastSc.RecentMessages, batch.ParticipantSnapshot);

            // 图片语义帧
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

            // 6. 路由处理
            if (isTask)
            {
                var taskMemory = memoryResults?.Where(m => !m.IsPersona).ToList();
                string? memoryContext = FormatMemory(taskMemory, topK: 10);

                workingCore.OnSpeak = async (rawText) =>
                {
                    await ctx.Adapters.SendMessageAsync(lastMsg.Platform, new OutgoingMessage
                    {
                        ChannelId = lastMsg.ChannelId,
                        Content = rawText
                    });
                    await ctx.Session.SaveBotMessageAsync(lastSc.Topic.Id, lastSc.Channel.Id, rawText);
                };
                workingCore.OnMemory = async (content) =>
                {
                    await ctx.MemorySvc.StoreAsync(content, lastSc.Person.Id, lastSc.Channel.Id, lastSc.Topic.Id);
                };
                workingCore.OnSignal = async (signalName, payload) =>
                {
                    ctx.EventBus.PublishSignal(signalName, payload);
                    await Task.CompletedTask;
                };
                workingCore.OnReviewHint = async (content) =>
                {
                    await ctx.ReviewHints.CreateAsync(content, lastSc.Person.Id, lastSc.Channel.Id, lastSc.Topic.Id);
                };

                // 设置消息通道：WorkingCore 运行期间可感知新消息
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
                    // 清理消息通道
                    this.activeMessageQueue = null;
                    this.activeMessageSignal = null;
                }
            }
            else
            {
                string? memoryContext = FormatMemory(memoryResults, topK: 5);

                var inputBuilder = new StringBuilder();
                inputBuilder.Append(formattedContext);
                if (memoryContext != null)
                {
                    inputBuilder.AppendLine();
                    inputBuilder.AppendLine();
                    inputBuilder.AppendLine("[记忆参考]");
                    inputBuilder.Append(memoryContext);
                }

                expressCore.ResetProcessor();
                var expressInput = inputBuilder.ToString();
                var expressed = imagePaths.Count > 0
                    ? await expressCore.GenerateOnceAsync(expressInput, imagePaths)
                    : await expressCore.GenerateOnceAsync(expressInput);

                await ctx.Adapters.SendMessageAsync(lastMsg.Platform, new OutgoingMessage
                {
                    ChannelId = lastMsg.ChannelId,
                    Content = expressed
                });
                await ctx.Session.SaveBotMessageAsync(lastSc.Topic.Id, lastSc.Channel.Id, expressed);
            }

            // 7. 记忆提取计数
            TrackMemoryExtraction(messages, lastSc);
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
                    personId, context.Channel.Id, topicId,
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
                var recent = await ctx.Session.GetContextAsync(topicId, limit: 10);
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
                            context.Person.Id, context.Channel.Id, topicId,
                            confidence: item.Confidence);
                        factCount++;
                    }
                }

                if (factCount + feedbackCount > 0)
                    FrameworkLogger.Log("WorkerEngine",
                        $"记忆提取: topicId={topicId}, 事实{factCount}条, 反馈{feedbackCount}条");
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

        // ---- XML 格式构建 ----

        private string BuildContextXml(
            List<(IncomingMessage Message, SessionContext Context)> batch,
            List<UserMessage> recentMessages,
            Dictionary<int, ParticipantInfo> participants)
        {
            var sb = new StringBuilder();
            var shortNames = ResolveShortNames(participants);

            // 1. 参与者声明
            sb.AppendLine("<participants>");
            foreach (var (userId, info) in participants)
            {
                var name = SanitizeAttr(shortNames.GetValueOrDefault(userId, info.DisplayName));
                var nick = SanitizeAttr(info.Nickname);
                sb.AppendLine($"  <user name=\"{name}\" nickname=\"{nick}\" qq=\"{info.PlatformId}\"/>");
            }
            sb.AppendLine("</participants>");

            // 2. 对话历史（已处理的消息 + 数据库历史，排除当前 batch）
            var batchTicks = new HashSet<long>(batch.Select(b => b.Message.Time.Ticks));
            var historyMessages = recentMessages
                .Where(m => !batchTicks.Contains(m.Time.Ticks))
                .ToList();

            if (historyMessages.Count > 0)
            {
                sb.AppendLine("<history>");
                foreach (var m in historyMessages)
                {
                    var name = m.IsFromBot ? "Lilara"
                             : ResolveHistoryShortName(m, shortNames);
                    sb.AppendLine($"<msg user=\"{SanitizeAttr(name)}\">{SanitizeContent(m.Content)}</msg>");
                }
                sb.AppendLine("</history>");
            }

            // 3. 新消息（当前 batch）
            sb.AppendLine("<new>");
            foreach (var (msg, sc) in batch)
            {
                var name = shortNames.GetValueOrDefault(sc.User.Id, sc.User.DisplayName);
                if (string.IsNullOrEmpty(name))
                    name = msg.DisplayName ?? msg.PlatformUserId;
                sb.AppendLine($"<msg user=\"{SanitizeAttr(name)}\">{SanitizeContent(msg.Content)}</msg>");
            }
            sb.Append("</new>");

            return sb.ToString();
        }

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
    }
}
