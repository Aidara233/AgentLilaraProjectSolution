using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Memory;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Engine.Modules;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 上下文构建：系统前缀、模式规则、消息格式化、对话历史、持久化。
    /// </summary>
    partial class ChannelEngine
    {
        private static string EscapeXml(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
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
                sb.AppendLine("[图片说明] 正文中的 [IMG:N] 标记表示该位置有图片。[图片描述] 后跟随图片的视觉内容描述。[图中文字] 后跟随OCR提取的文字。文字较长时会被截断，可使用 get_image_text 工具传入图片hash查看全文。相同图片不会重复出现。");
            }
            else
            {
                sb.AppendLine(ToolRegistry.GenerateDescriptions(
                    additionalTools: ctx.GlobalComponentHost?.GetVisibleTools("channel").ToList()));
                var botId = ctx.Adapters.GetBotPlatformId("qq");
                if (!string.IsNullOrEmpty(botId))
                    sb.AppendLine($"\n身份信息：你的QQ号是 {botId}。");
                sb.AppendLine(FormatChannelContext());
                sb.AppendLine("\n[图片说明] 正文中的 [IMG:N] 标记表示该位置有图片，下方紧随对应的 [IMG:N] + 实际图片。[图中文字] 后跟随OCR提取的文字。文字较长时会被截断，可使用 get_image_text 工具传入图片hash查看全文。相同图片不会重复出现。");
            }

            sb.AppendLine();
            sb.AppendLine(BuildModeRules());

            return sb.ToString();
        }

        private string BuildModeRules()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[当前模式] Express (express)");
            sb.AppendLine("轻量对话模式。工具：speak, send_media, escalate, wait, memory");
            sb.AppendLine("[模式规则] 需要复杂操作时用 escalate 切换到工作会话。");
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
                "group" => $"当前频道：群聊，群号: {id}，频道ID: {channelId}。对群成员操作时需提供 group_id={id}。",
                "private" => $"当前频道：私聊，对方QQ: {id}，频道ID: {channelId}。",
                _ => ""
            };
        }

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
            parts.Add($"db_id=\"{m.Id}\"");
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

        /// <summary>按 [IMG:N] 标记拆分文本，交错插入 [图N] 标签 + 图片。同 hash 去重。</summary>
        private async Task<List<ContentPart>> BuildInterleavedContentParts(
            string text, IEnumerable<UserMessage> msgs, HashSet<string> seenHashes)
        {
            // 收集图片路径（顺序与 [IMG:N] 对应）
            var imagePaths = new List<string>();
            var imageHashes = new List<string>();
            foreach (var m in msgs)
            {
                if (string.IsNullOrEmpty(m.ImageHashes)) continue;
                foreach (var hash in m.ImageHashes.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = hash.Trim();
                    var path = await ImageStorage.GetModelInputPathAsync(trimmed);
                    imagePaths.Add(path ?? "");
                    imageHashes.Add(trimmed);
                }
            }

            var regex = new Regex(@"\[IMG:(\d+)\]");
            var matches = regex.Matches(text);
            var parts = new List<ContentPart>();
            int lastEnd = 0;

            foreach (Match match in matches)
            {
                // 标记前的文本
                if (match.Index > lastEnd)
                    parts.Add(ContentPart.FromText(text[lastEnd..match.Index]));

                int imgIndex = int.Parse(match.Groups[1].Value);
                if (imgIndex >= 0 && imgIndex < imagePaths.Count && !string.IsNullOrEmpty(imagePaths[imgIndex]))
                {
                    var hash = imageHashes[imgIndex];
                    if (seenHashes.Contains(hash))
                    {
                        // 重复图片：只引用标签
                        parts.Add(ContentPart.FromText($"[IMG:{imgIndex}]"));
                    }
                    else
                    {
                        // 首次出现：标记 + 图片 + 描述
                        seenHashes.Add(hash);
                        _roundImageHashes.Add(hash);
                        parts.Add(ContentPart.FromText($"[IMG:{imgIndex}]"));
                        parts.Add(ContentPart.FromImagePath(imagePaths[imgIndex]));
                        var desc = await ImageStorage.GetDescriptionAsync(hash);
                        if (!string.IsNullOrEmpty(desc))
                        {
                            _injectedDescriptions.Add(hash);
                            parts.Add(ContentPart.FromText($"[图片描述] {desc}"));
                        }
                        var ocrInjection = await BuildOcrInjectionTextAsync(hash, true);
                        if (!string.IsNullOrEmpty(ocrInjection))
                        {
                            _injectedOcrTexts.Add(hash);
                            parts.Add(ContentPart.FromText(ocrInjection));
                        }
                    }
                }

                lastEnd = match.Index + match.Length;
            }

            // 剩余文本
            if (lastEnd < text.Length)
                parts.Add(ContentPart.FromText(text[lastEnd..]));

            if (parts.Count == 0)
                parts.Add(ContentPart.FromText(text));

            return parts;
        }

        /// <summary>将文本 + 一组图片 hash 列表组装为 ContentParts。</summary>
        private async Task<List<ContentPart>> BuildContentPartsWithImagePaths(string text, IEnumerable<string> imagePaths, List<string>? imageHashes = null)
        {
            var parts = new List<ContentPart> { ContentPart.FromText(text) };
            var hashes = imageHashes ?? new List<string>();
            for (int i = 0; i < imagePaths.Count(); i++)
            {
                var path = imagePaths.ElementAt(i);
                if (!string.IsNullOrEmpty(path))
                {
                    parts.Add(ContentPart.FromImagePath(path));
                    if (i < hashes.Count)
                    {
                        var desc = await ImageStorage.GetDescriptionAsync(hashes[i]);
                        if (!string.IsNullOrEmpty(desc))
                            parts.Add(ContentPart.FromText($"[图片描述] {desc}"));
                        var ocrInjection = await BuildOcrInjectionTextAsync(hashes[i], true);
                        if (!string.IsNullOrEmpty(ocrInjection))
                            parts.Add(ContentPart.FromText(ocrInjection));
                    }
                }
            }
            return parts;
        }

        public async Task<List<Message>?> BuildStartInjectAsync()
        {
            var msgs = new List<Message>();

            // ═══ FRAMEWORK: 每次会话重新生成，不持久化 ═══
            // （只有人为可能调整的内容放在此处，其余全部走持久化以最大化缓存利用率）

            // 1. 框架前缀（系统配置、工具描述等）
            if (fixedPrefix == null) fixedPrefix = BuildFixedPrefix();
            msgs.Add(new Message { Role = "user", Content = fixedPrefix });

            // 1.5 叫醒提示（仅第一轮注入，注入后立即消费）
            if (_justWokenUp)
            {
                _justWokenUp = false;
                msgs.Add(new Message { Role = "user", Content = "[系统] 你刚被吵醒，还没完全清醒，语气带着起床气和迷糊。" });
            }

            // 2. 上下文摘要（压缩后的历史）
            if (!string.IsNullOrEmpty(contextSummary))
                msgs.Add(new Message { Role = "user", Content = $"[上下文摘要]\n{contextSummary}" });

            // 3. 参与者列表注入（名字、平台ID、内部ID、信任等级、权限）
            if (currentParticipantSnapshot != null && currentParticipantSnapshot.Count > 0)
            {
                var partSb = new StringBuilder();
                // 频道基本信息（Express 模式）
                if (!string.IsNullOrEmpty(channelName))
                {
                    var chParts = channelName.Split('_', 2);
                    if (chParts.Length == 2)
                    {
                        var chType = chParts[0];
                        var chId = chParts[1];
                        partSb.AppendLine($"[频道] 类型: {chType}, 平台ID: {chId}, 频道ID: {channelId}");
                    }
                }
                partSb.AppendLine("[当前参与者]");
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

            // 4. IInjectProvider start injections
            var iCtx = new InjectContext
            {
                Mode = "express",
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
                catch (Exception ex) { Signal.Warn(LogGroup.Engine, $"InjectProvider.Start 失败: {p.GetType().Name}", new { provider = p.GetType().Name, error = ex.Message }); }
            }

            // 5. BuildComponentInjections — 使用全局组件宿主替代本地 ComponentHost
            BuildComponentInjections(msgs);

            // ═══ CONVERSATION ═══

            // 6. 初始历史：从 DB 拉取历史消息（Express 格式）
            {
                _startInjectMaxId = _lastConsumedMessageId;
                var recentMsgs = await ctx.Session.GetContextByChannelAsync(channelId, HistoryMaxMessages);
                if (recentMsgs.Count > 0)
                {
                    var historyMsgs = recentMsgs.Where(m => m.Id <= _lastConsumedMessageId).ToList();
                    if (historyMsgs.Count > 0)
                    {
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
                        {
                            var seenHashes = new HashSet<string>();
                            var parts = await BuildInterleavedContentParts(histSb.ToString(), historyMsgs, seenHashes);
                            var msg = new Message { Role = "user", Content = histSb.ToString() };
                            if (parts.Count > 1) msg.ContentParts = parts;
                            msgs.Add(msg);
                        }
                    }

                    _startInjectMaxId = historyMsgs.Count > 0 ? historyMsgs.Max(m => m.Id) : _lastConsumedMessageId;
                }
            }

            // 7. 记忆检索
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

            Signal.Event(LogGroup.Engine, "上下文组装完成", new
            {
                channelId,
                mode = "express",
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
                            var imgHashes = new List<string>();
                            foreach (var att in nms.Message.Attachments.Where(a => a.Type == AttachmentType.Image && !string.IsNullOrEmpty(a.Hash)))
                            {
                                var p = await ImageStorage.GetModelInputPathAsync(att.Hash!);
                                if (!string.IsNullOrEmpty(p)) { imgPaths.Add(p); imgHashes.Add(att.Hash!); }
                            }
                            if (imgPaths.Count > 0)
                            {
                                var parts = await BuildContentPartsWithImagePaths(nmsText, imgPaths, imgHashes);
                                if (parts.Count > 1) nmsMsg.ContentParts = parts;
                            }

                            // 文件附件：追加 URL/FileId 和元数据，模型可用 download_file 或 download_chat_file 下载
                            var fileAtts = nms.Message.Attachments
                                .Where(a => a.Type == AttachmentType.File &&
                                    (!string.IsNullOrEmpty(a.SourceUrl) || !string.IsNullOrEmpty(a.FileId)))
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
                                    var urlOrId = !string.IsNullOrEmpty(fa.SourceUrl)
                                        ? $"url={fa.SourceUrl}"
                                        : $"file_id={fa.FileId}";
                                    fileLines.AppendLine($"- {fa.FileName ?? "未知文件"} ({sizeStr}) {urlOrId}");
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
                        // Express 模式：仅更新上下文摘要，不重建 Agent
                        contextSummary = cs.Summary;
                        break;
                    case ModeSwitchSignal mss:
                        // 模式切换信号（Working 已移除，仅保留通知）
                        if (!string.IsNullOrEmpty(mss.Reason))
                            msgs.Add(new Message { Role = "user", Content = $"[系统] 模式切换通知：{mss.Reason}" });
                        break;
                }
            }

            // 统一新消息追赶：从 DB 拉取游标之后的全量消息（所有模式生效）
            // 用 Math.Max 跳过 BuildStartInjectAsync 已注入的消息
            // 游标为 0 时（冷启动）拉取最近 HistoryMaxMessages 条，后续轮次拉增量
            var effectiveCursor = Math.Max(_lastConsumedMessageId, _startInjectMaxId);
            {
                var rawNewMsgs = await ctx.Session.GetLatestMessagesAfterIdAsync(channelId, effectiveCursor, HistoryMaxMessages);
                if (rawNewMsgs.Count > 0)
                {
                    var allNewMsgs = rawNewMsgs;
                    var newMsgs = rawNewMsgs;
                    // 游标为 0 时裁剪到 HistoryMaxMessages，避免加载全库
                    if (effectiveCursor == 0 && newMsgs.Count > HistoryMaxMessages)
                    {
                        newMsgs = newMsgs.Skip(newMsgs.Count - HistoryMaxMessages).ToList();
                    }
                    // Express：裁剪只保留最近窗口，避免上下文爆炸
                    if (newMsgs.Count > HistoryMaxMessages)
                    {
                        newMsgs = newMsgs.Skip(newMsgs.Count - HistoryMaxMessages).ToList();
                    }

                    // Express 格式
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
                        var roundQc = await BuildQuotedContextForBatchAsync(newMsgs, channelId, maxDepth: 2, includeSurrounding: true);
                        if (roundQc != null)
                            await AddQuotedContextMessage(msgs, roundQc.Value.Text, roundQc.Value.ImagePaths);

                        {
                            var seenHashes = new HashSet<string>();
                            var parts = await BuildInterleavedContentParts(sb.ToString(), newMsgs, seenHashes);
                            var msg = new Message { Role = "user", Content = sb.ToString() };
                            if (parts.Count > 1) msg.ContentParts = parts;
                            msgs.Add(msg);
                        }
                    }

                    // 游标推进到所有新消息（包括被裁剪的）
                    var maxNewId = allNewMsgs.Max(m => m.Id);
                    if (maxNewId > _lastConsumedMessageId)
                        _lastConsumedMessageId = maxNewId;

                    // 同步扣减缓冲计数：已消费的消息不应再触发外层循环
                    lock (bufferLock)
                    {
                        _bufferedMessageCount = Math.Max(0, _bufferedMessageCount - allNewMsgs.Count);
                    }
                }
            }

            // IInjectProvider round injections
            var roundCtx = new InjectContext
            {
                Mode = "express",
                CurrentRound = agent?.TotalRounds ?? 1,
                MaxRounds = agentConfig.MaxRounds,
                EstimatedTokens = 0
            };

            // 滞后描述补注：本轮图片的描述可能在后续轮次才就绪
            foreach (var hash in _roundImageHashes)
            {
                if (_injectedDescriptions.Contains(hash)) continue;
                var desc = await ImageStorage.GetDescriptionAsync(hash);
                if (!string.IsNullOrEmpty(desc))
                {
                    _injectedDescriptions.Add(hash);
                    msgs.Add(new Message { Role = "user", Content = $"[图片描述] 之前图片的描述已就绪：{desc}" });
                }
            }

            // 滞后 OCR 补注
            foreach (var hash in _roundImageHashes)
            {
                if (_injectedOcrTexts.Contains(hash)) continue;
                var ocrInjection = await BuildOcrInjectionTextAsync(hash, true);
                if (!string.IsNullOrEmpty(ocrInjection))
                {
                    _injectedOcrTexts.Add(hash);
                    msgs.Add(new Message { Role = "user", Content = ocrInjection });
                }
            }

            return msgs.Count > 0 ? msgs : null;
        }

        private void BuildComponentInjections(List<Message> msgs)
        {
            if (ctx.GlobalComponentHost != null)
            {
                var groups = ToolListFormatter.CollectGroups(null, ctx.GlobalComponentHost);
                var overview = ToolListFormatter.BuildToolOverviewSection(groups, "express");
                if (!string.IsNullOrEmpty(overview))
                    msgs.Add(new Message { Role = "user", Content = overview });

                var globalSections = ctx.GlobalComponentHost.BuildPromptSections(
                    new LoopInfo(channelId.ToString(), "channel"));
                foreach (var s in globalSections)
                    if (!string.IsNullOrEmpty(s))
                        msgs.Add(new Message { Role = "user", Content = s });
            }
        }

        private static bool IsEmptyMessage(Message m)
            => string.IsNullOrEmpty(m.Content) && (m.ContentParts == null || m.ContentParts.Count == 0);
    }
}
