using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Memory;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 频道记忆提取 Worker。独立信号树，fire-and-forget 执行，
    /// cause 指向触发 session。每次调用创建新信号，支持并行提取可视化。
    /// </summary>
    internal class ChannelExtractionWorker
    {
        private readonly ISystemContext ctx;
        private readonly int channelId;
        private readonly ChannelConfig channelConfig;
        private readonly ConcurrentDictionary<int, ParticipantInfo> recentParticipants;
        private readonly Func<DateTime?> getLastCompletionTime;

        private int lastExtractedMessageId = -1; // -1 = 未初始化
        private int latestMessageId;
        private int totalMessageCount;
        private int extractedMessageCount;
        private volatile bool running;
        private CancellationTokenSource? cts;

        public bool IsRunning => running;
        public int LastExtractedMessageId => lastExtractedMessageId < 0 ? 0 : lastExtractedMessageId;
        public int LatestMessageId => latestMessageId;
        public int TotalMessageCount => totalMessageCount;
        public int ExtractedMessageCount => extractedMessageCount;

        public ChannelExtractionWorker(ISystemContext ctx, int channelId,
            ChannelConfig config, ConcurrentDictionary<int, ParticipantInfo> participants,
            Func<DateTime?> getLastCompletionTime)
        {
            this.ctx = ctx;
            this.channelId = channelId;
            this.channelConfig = config;
            this.recentParticipants = participants;
            this.getLastCompletionTime = getLastCompletionTime;
        }

        public void SetAutoExtraction(bool enabled)
        {
            channelConfig.AutoExtractionEnabled = enabled;
            ChannelStateManager.SaveConfig(channelId, channelConfig);
        }

        public void Cancel()
        {
            cts?.Cancel();
        }

        public void Trigger(SessionContext context, string? causeSpanId)
        {
            if (!channelConfig.AutoExtractionEnabled || running) return;
            StartRun(context, causeSpanId, force: false);
        }

        public void ForceTrigger(SessionContext context, string? causeSpanId)
        {
            if (running) return;
            StartRun(context, causeSpanId, force: true);
        }

        private void StartRun(SessionContext context, string? causeSpanId, bool force)
        {
            running = true;

            _ = Task.Run(async () =>
            {
                SignalContext.Restore(null);
                using var extCtx = Signal.Continue(
                    SignalContext.NewSignalId(), causeSpanId,
                    $"extraction:channel:{channelId}", LogGroup.Memory,
                    force ? "强制记忆提取" : "记忆提取", new { channelId, force });

                try
                {
                    await RunAsync(context, force);
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Memory, "记忆提取异常",
                        new { channelId, error = ex.GetType().Name, message = ex.Message });
                }
                finally
                {
                    running = false;
                    extCtx.Close(new { channelId });
                }
            });
        }

        private async Task RunAsync(SessionContext context, bool force = false)
        {
            cts = new CancellationTokenSource();
            var ct = cts.Token;

            // 首次运行时从 DB 加载持久化进度
            if (lastExtractedMessageId < 0)
            {
                var channel = await ctx.Session.GetChannelAsync(channelId);
                lastExtractedMessageId = channel?.LastExtractedMessageId ?? 0;
                Signal.Event(LogGroup.Memory, "加载提取进度",
                    new { channelId, lastExtractedMessageId });
            }

            totalMessageCount = await ctx.Session.GetMessageCountByChannelAsync(channelId);
            extractedMessageCount = await ctx.Session.GetMessageCountUpToAsync(
                channelId, lastExtractedMessageId);

            while (!ct.IsCancellationRequested)
            {
                totalMessageCount = await ctx.Session.GetMessageCountByChannelAsync(channelId);
                extractedMessageCount = await ctx.Session.GetMessageCountUpToAsync(
                    channelId, lastExtractedMessageId);

                var newMessages = await ctx.Session.GetMessagesAfterIdAsync(
                    channelId, lastExtractedMessageId, limit: 50);
                if (newMessages.Count < 2) break;

                latestMessageId = newMessages[^1].Id;

                // 判断活跃/潜水阈值
                var lastTime = getLastCompletionTime();
                bool isActive = lastTime != null
                    && (DateTime.Now - lastTime.Value).TotalMinutes < 5;
                int threshold = isActive
                    ? channelConfig.ActiveExtractionThreshold
                    : channelConfig.LurkingExtractionThreshold;

                if (!force && newMessages.Count < threshold) break;

                var contextMessages = lastExtractedMessageId > 0
                    ? await ctx.Session.GetMessagesBeforeIdAsync(channelId, lastExtractedMessageId, limit: 20)
                    : new List<UserMessage>();

                var recentMems = await ctx.TempMemories.GetRecentByChannelAsync(channelId, 10);
                var recentMemContents = recentMems.Count > 0
                    ? recentMems.ConvertAll(m => m.Content)
                    : null;

                var contextLines = contextMessages.Select(FormatMessageLine).ToList();
                var newLines = newMessages.Select(FormatMessageLine).ToList();

                var participantNames = recentParticipants.Values
                    .Select(p => p.DisplayName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct().ToList();
                if (participantNames.Count > 0)
                    contextLines.Insert(0, $"[群聊参与者: {string.Join("、", participantNames)}]");

                // 每批提取 span
                int count;
                using (var batchSpan = Signal.Open(LogGroup.Memory, "提取批次",
                    new { channelId, newMsgCount = newMessages.Count, contextMsgCount = contextMessages.Count }))
                {
                    var core = new MemoryExtractionCore();
                    var results = await core.ExtractAsync(contextLines, newLines, recentMemContents);

                    count = 0;
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

                    batchSpan.SetCloseDetail(new
                    {
                        extractedCount = count,
                        results = results.Select(r => new
                        {
                            type = r.Type,
                            content = r.Content,
                            subject = r.Subject,
                            confidence = r.Confidence
                        })
                    });
                }

                lastExtractedMessageId = newMessages[^1].Id;
                await ctx.Session.UpdateExtractionProgressAsync(channelId, lastExtractedMessageId);
                extractedMessageCount = await ctx.Session.GetMessageCountUpToAsync(
                    channelId, lastExtractedMessageId);

                Signal.Event(LogGroup.Memory, "提取进度更新", new
                {
                    channelId,
                    lastExtractedMessageId,
                    remaining = totalMessageCount - extractedMessageCount
                });

                if (newMessages.Count < 50) break;
            }

            Signal.Event(LogGroup.Memory, "提取结束", new { channelId, extractedCount = extractedMessageCount });
        }

        private static string FormatMessageLine(UserMessage m)
        {
            if (m.IsFromBot) return $"Lilara: {m.Content}";
            var name = !string.IsNullOrEmpty(m.SenderName) ? m.SenderName : "用户";
            return $"{name}(#{m.UserId}): {m.Content}";
        }

        private int? ResolveAboutToPersonId(string? about)
        {
            if (string.IsNullOrEmpty(about)) return null;

            if (about.StartsWith('#') && int.TryParse(about[1..], out var userId))
            {
                if (recentParticipants.TryGetValue(userId, out var info))
                    return info.PersonId;
            }

            var hashIdx = about.IndexOf('#');
            if (hashIdx >= 0)
            {
                var idPart = about[(hashIdx + 1)..].TrimEnd(')');
                if (int.TryParse(idPart, out var uid) && recentParticipants.TryGetValue(uid, out var info2))
                    return info2.PersonId;
            }

            foreach (var (_, p) in recentParticipants)
                if (p.DisplayName.Equals(about, StringComparison.OrdinalIgnoreCase))
                    return p.PersonId;

            return null;
        }
    }
}
