using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 频道记忆提取 Worker。独立信号树，fire-and-forget 执行，
    /// cause 指向触发 span。每次调用创建新信号，支持并行提取可视化。
    /// 引擎无关：participants 自 DB 派生，活跃/潜水阈值靠最近 bot 出向消息时间判定。
    /// </summary>
    internal class ChannelExtractionWorker
    {
        private readonly ISystemContext ctx;
        private readonly int channelId;
        private readonly ChannelConfig channelConfig;
        /// <summary>参与者列表从 ChannelContextService 派生（DB 派生，无累积状态）。</summary>
        private readonly ChannelContextService ctxService;

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

        public ChannelExtractionWorker(ISystemContext ctx, int channelId, ChannelConfig config)
        {
            this.ctx = ctx;
            this.channelId = channelId;
            this.channelConfig = config;
            this.ctxService = new ChannelContextService(new SystemContextDataSource(ctx));
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

        public void Trigger(string? causeSpanId)
        {
            if (!channelConfig.AutoExtractionEnabled || running) return;
            StartRun(causeSpanId, force: false);
        }

        public void ForceTrigger(string? causeSpanId)
        {
            if (running) return;
            StartRun(causeSpanId, force: true);
        }

        private void StartRun(string? causeSpanId, bool force)
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
                    await RunAsync(force);
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

        private async Task RunAsync(bool force = false)
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

            // 从 ChannelContextService 获取参与者列表（DB 派生）
            List<ParticipantView> participants;
            try { participants = await ctxService.GetParticipantsAsync(channelId); }
            catch { participants = new List<ParticipantView>(); }

            while (!ct.IsCancellationRequested)
            {
                totalMessageCount = await ctx.Session.GetMessageCountByChannelAsync(channelId);
                extractedMessageCount = await ctx.Session.GetMessageCountUpToAsync(
                    channelId, lastExtractedMessageId);

                var newMessages = await ctx.Session.GetMessagesAfterIdAsync(
                    channelId, lastExtractedMessageId, limit: 50);
                if (newMessages.Count < 2) break;

                latestMessageId = newMessages[^1].Id;

                // 判断活跃/潜水阈值：最近 5 分钟内有 bot 出向消息 = 活跃（方案 A，引擎无关）
                var lastBotTime = await ctx.Session.GetLatestBotMessageTimeAsync(channelId);
                bool isActive = lastBotTime != null
                    && (DateTime.Now - lastBotTime.Value).TotalMinutes < 5;
                int threshold = isActive
                    ? channelConfig.ActiveExtractionThreshold
                    : channelConfig.LurkingExtractionThreshold;

                if (!force && newMessages.Count < threshold) break;

                var contextMessages = lastExtractedMessageId > 0
                    ? await ctx.Session.GetMessagesBeforeIdAsync(channelId, lastExtractedMessageId, limit: 20)
                    : new List<UserMessage>();

                // 旧记忆提取内核已退役（记忆重构完成，MemoryExtractionCore/MemorySvc 已删）。
                // 新伴随提取由 MindEngine.ExtractService 承担，这里仅推进进度标记。
                var newLines = newMessages.Select(FormatMessageLine).ToList();
                if (newLines.Count > 0)
                {
                    Signal.Event(LogGroup.Memory, "提取(新链路待接入)",
                        new { channelId, newMsgCount = newLines.Count });
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

        /// <summary>该批最后发言的非 bot 用户的 PersonId（about 解析失败时的兜底归属）。无则 null。</summary>
        private async Task<int?> ResolveFallbackPersonId(List<UserMessage> batch)
        {
            for (int i = batch.Count - 1; i >= 0; i--)
            {
                var m = batch[i];
                if (m.IsFromBot || m.UserId <= 0) continue;
                var user = await ctx.Session.GetUserByIdAsync(m.UserId);
                if (user != null) return user.PersonId;
            }
            return null;
        }

        private int? ResolveAboutToPersonId(string? about, List<ParticipantView> participants)
        {
            if (string.IsNullOrEmpty(about)) return null;

            // 支持 "#userId" 格式（如 "张三#5678" 或 "#5678"）：解析末尾 #数字 部分
            var hashIdx = about.LastIndexOf('#');
            if (hashIdx >= 0 && int.TryParse(about.AsSpan(hashIdx + 1), out int userId) && userId > 0)
            {
                var byId = participants.FirstOrDefault(p => p.UserId == userId);
                if (byId != null) return byId.PersonId;
            }

            // 按 DisplayName 模糊匹配（OrdinalIgnoreCase）
            foreach (var p in participants)
                if (p.DisplayName.Equals(about, StringComparison.OrdinalIgnoreCase))
                    return p.PersonId;

            return null;
        }
    }
}
