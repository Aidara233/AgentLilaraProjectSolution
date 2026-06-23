// AgentCoreProcessor/Engine/Context/ChannelContextService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Vision;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>频道上下文的一个参与者视图（DB 派生，无累积状态）。</summary>
internal record ParticipantView(
    string DisplayName, string? Aliases, string PlatformId,
    int PersonId, int TrustLevel, string? Memo, int UserId = 0);

/// <summary>
/// 频道上下文构建服务。ChannelEngine 与 RuntimeSession 共用。
/// 纯格式化方法静态；依赖 DB 的方法为实例方法，注入 IChannelDataSource。
/// </summary>
internal partial class ChannelContextService
{
    private readonly IChannelDataSource _data;
    public ChannelContextService(IChannelDataSource data) => _data = data;

    // ═══════════════════════════════════════════════════════════
    // 参与者派生
    // ═══════════════════════════════════════════════════════════

    /// <summary>从频道最近消息派生当前参与者（distinct 发送者，排除 bot）。无累积状态。</summary>
    public async Task<List<ParticipantView>> GetParticipantsAsync(int channelId, int limit = 30)
    {
        var result = new List<ParticipantView>();
        var seen = new HashSet<int>();
        var msgs = await _data.GetRecentMessagesAsync(channelId, limit);
        foreach (var m in msgs)
        {
            if (m.IsFromBot || m.UserId <= 0 || !seen.Add(m.UserId)) continue;
            var user = await _data.GetUserByIdAsync(m.UserId);
            if (user == null) continue;
            var person = await _data.GetPersonByIdAsync(user.PersonId);
            var displayName = !string.IsNullOrEmpty(person?.Name) ? person!.Name
                : !string.IsNullOrEmpty(user.DisplayName) ? user.DisplayName
                : user.PlatformId;
            result.Add(new ParticipantView(
                displayName, person?.Aliases, user.PlatformId,
                user.PersonId, (int)(person?.TrustLevel ?? Database.TrustLevel.Unknown), person?.FastMemory, user.Id));
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════
    // 静态纯格式化
    // ═══════════════════════════════════════════════════════════

    /// <summary>渲染频道信息行（群号/私聊对象/频道ID）。channelName 格式 "group_xxx"/"private_xxx"。</summary>
    public static string RenderChannelInfo(string? channelName, int channelId)
    {
        if (string.IsNullOrEmpty(channelName)) return "";
        var parts = channelName.Split('_', 2);
        if (parts.Length != 2) return "";
        var id = parts[1];
        return parts[0] switch
        {
            "group" => $"当前频道：群聊，群号: {id}，频道ID: {channelId}。对群成员操作时需提供 group_id={id}。",
            "private" => $"当前频道：私聊，对方QQ: {id}，频道ID: {channelId}。",
            _ => ""
        };
    }

    /// <summary>渲染参与者列表块。空列表返回空串。</summary>
    public static string RenderParticipants(
        string? channelName, int channelId, IReadOnlyList<ParticipantView> participants)
    {
        if (participants.Count == 0) return "";
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(channelName))
        {
            var p = channelName.Split('_', 2);
            if (p.Length == 2)
                sb.AppendLine($"[频道] 类型: {p[0]}, 平台ID: {p[1]}, 频道ID: {channelId}");
        }
        sb.AppendLine("[当前参与者]");
        foreach (var p in participants)
        {
            sb.Append($"- {p.DisplayName}");
            if (!string.IsNullOrEmpty(p.Aliases)) sb.Append($" (别称:{p.Aliases})");
            sb.Append($" [platform_id:{p.PlatformId}, person_id:{p.PersonId}, trust:{p.TrustLevel}]");
            if (!string.IsNullOrEmpty(p.Memo)) sb.Append($" — {p.Memo}");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════
    // 静态辅助（XML / 属性格式化）
    // ═══════════════════════════════════════════════════════════

    /// <summary>XML 字符转义。</summary>
    public static string EscapeXml(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    /// <summary>检查 DB 消息中 bot 是否被 @提及。</summary>
    public bool IsBotMentionedInMessage(UserMessage m)
    {
        if (string.IsNullOrEmpty(m.MentionedPlatformIds)) return false;
        var botId = _data.GetBotPlatformId("qq");
        if (string.IsNullOrEmpty(botId)) return false;
        return m.MentionedPlatformIds.Split(',').Any(id => id == botId);
    }

    /// <summary>为 Working XML &lt;message&gt; 构建额外属性（id / reply / mentioned / mentioned_users）。</summary>
    public string FormatMessageAttrs(UserMessage m)
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

    // ═══════════════════════════════════════════════════════════
    // OCR 辅助（静态，仅依赖 ImageStorage + VisionEngineConfig）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 为指定图片构建 OCR 文字注入文本。根据文字长度和模式决定注入形式。
    /// 返回 null 表示无需注入（无 OCR 或 HasText 未知）。
    /// </summary>
    private static async Task<string?> BuildOcrInjectionTextAsync(string hash, bool isExpress)
    {
        var record = await ImageStorage.GetByHashAsync(hash);
        if (record?.HasText != true) return null;
        var ocrText = record.OcrText;
        if (string.IsNullOrEmpty(ocrText)) return null;

        var threshold = VisionEngineConfig.Load().OcrRichTextThreshold;
        if (ocrText.Length <= threshold || isExpress)
        {
            return $"[图中文字] {ocrText}";
        }
        else
        {
            var preview = ocrText[..threshold];
            return $"[图中文字] 文字较长({ocrText.Length}字)，预览：{preview}... 使用 get_image_text {hash} 查看全文";
        }
    }

    // ═══════════════════════════════════════════════════════════
    // quoted-context 辅助
    // ═══════════════════════════════════════════════════════════

    /// <summary>为一组消息批次构建缺失引用的 &lt;quoted-context&gt; 块（递归展开）。返回文本 + 收集到的图片路径。</summary>
    public async Task<(string Text, List<string> ImagePaths)?> BuildQuotedContextForBatchAsync(
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
    public async Task AppendQuotedContextRecursiveAsync(
        StringBuilder sb, List<string> imagePaths, string targetId, int channelId,
        int remainingDepth, HashSet<string> included, bool includeSurrounding)
    {
        if (remainingDepth <= 0 || string.IsNullOrEmpty(targetId) || included.Contains(targetId))
            return;

        included.Add(targetId);

        var target = await _data.GetByPlatformMessageIdAsync(channelId, targetId);
        if (target == null) return;

        // 收集目标消息的图片路径
        await CollectImagePaths(target, imagePaths);

        sb.AppendLine("<quoted-context>");

        if (includeSurrounding)
        {
            var surrounding = await _data.GetContextAroundAsync(target.Id, channelId, 3);
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
    public static async Task CollectImagePaths(UserMessage m, List<string> imagePaths)
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
    public async Task AddQuotedContextMessage(List<Message> msgs, string text, List<string> imagePaths)
    {
        var msg = new Message { Role = "user", Content = text };
        if (imagePaths.Count > 0)
        {
            var parts = await BuildContentPartsWithImagePaths(text, imagePaths);
            if (parts.Count > 1) msg.ContentParts = parts;
        }
        msgs.Add(msg);
    }

    // ═══════════════════════════════════════════════════════════
    // 图片交错 / ContentParts 组装
    // ═══════════════════════════════════════════════════════════

    /// <summary>按 [IMG:N] 标记拆分文本，交错插入 [图N] 标签 + 图片。同 hash 去重（更新 st 哈希集合）。</summary>
    public async Task<List<ContentPart>> BuildInterleavedContentParts(
        string text, IEnumerable<UserMessage> msgs, HashSet<string> seenHashes, ChannelContextState st)
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
                    st.RoundImageHashes.Add(hash);
                    parts.Add(ContentPart.FromText($"[IMG:{imgIndex}]"));
                    parts.Add(ContentPart.FromImagePath(imagePaths[imgIndex]));
                    var desc = await ImageStorage.GetDescriptionAsync(hash);
                    if (!string.IsNullOrEmpty(desc))
                    {
                        st.InjectedDescriptions.Add(hash);
                        parts.Add(ContentPart.FromText($"[图片描述] {desc}"));
                    }
                    var ocrInjection = await BuildOcrInjectionTextAsync(hash, true);
                    if (!string.IsNullOrEmpty(ocrInjection))
                    {
                        st.InjectedOcrTexts.Add(hash);
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

    /// <summary>将文本 + 一组图片路径列表组装为 ContentParts（含描述/OCR）。</summary>
    public async Task<List<ContentPart>> BuildContentPartsWithImagePaths(
        string text, IEnumerable<string> imagePaths, List<string>? imageHashes = null)
    {
        var parts = new List<ContentPart> { ContentPart.FromText(text) };
        var hashes = imageHashes ?? new List<string>();
        var pathList = imagePaths.ToList();
        for (int i = 0; i < pathList.Count; i++)
        {
            var path = pathList[i];
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

    // ═══════════════════════════════════════════════════════════
    // 三大公共方法（Task 4 产出，供 Task 5 / Task 8 消费）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 构建初始历史消息块（含 quoted-context + 图片交错）。
    /// 对应 ChannelContextBuilder.cs 402-435 的历史拉取段。
    /// 字段映射：_lastConsumedMessageId→st.LastConsumedMessageId，_startInjectMaxId→st.StartInjectMaxId（读写均回写 st）。
    /// </summary>
    public async Task<List<Message>> BuildHistoryBlockAsync(ChannelContextState st)
    {
        var msgs = new List<Message>();
        st.StartInjectMaxId = st.LastConsumedMessageId;

        var recentMsgs = await _data.GetContextByChannelAsync(st.ChannelId, 20);
        if (recentMsgs.Count > 0)
        {
            // Express：游标筛选（Id<=cursor 之前的算历史，之后的留给 RoundInject 追赶段）
            // Session：忽略游标，取最近 N 条全量快照（escalate 出来的 session 游标恒 0，不能按游标筛）
            var historyMsgs = st.Mode == "session"
                ? recentMsgs
                : recentMsgs.Where(m => m.Id <= st.LastConsumedMessageId).ToList();
            if (historyMsgs.Count > 0)
            {
                var histQc = await BuildQuotedContextForBatchAsync(historyMsgs, st.ChannelId, maxDepth: 1, includeSurrounding: true);
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
                    var parts = await BuildInterleavedContentParts(histSb.ToString(), historyMsgs, seenHashes, st);
                    var msg = new Message { Role = "user", Content = histSb.ToString() };
                    if (parts.Count > 1) msg.ContentParts = parts;
                    msgs.Add(msg);
                }
            }

            st.StartInjectMaxId = historyMsgs.Count > 0 ? historyMsgs.Max(m => m.Id) : st.LastConsumedMessageId;
        }

        return msgs;
    }

    /// <summary>
    /// 将单条 DB 消息格式化为 &lt;new_messages&gt; XML 块（含 mention 前缀、reply 补块、图片 ContentParts）。
    /// 对应 ChannelContextBuilder.cs 中 FormatIncomingAsync 逻辑。
    /// 返回 null 表示该消息无需格式化输出（预留，当前总是返回 Message）。
    /// 注意：reply 补块（quoted-context）由调用方负责在此消息前用 AppendQuotedContextRecursiveAsync 单独构建，
    /// 因为补块需要插入到 nmsMsg 之前的消息列表中。
    /// </summary>
    public async Task<Message?> FormatIncomingAsync(ChannelContextState st, UserMessage m)
    {
        var name = m.IsFromBot ? "Lilara" : EscapeXml(m.SenderName);
        var attrs = new List<string>();
        if (!string.IsNullOrEmpty(m.PlatformMessageId))
            attrs.Add($"id=\"{EscapeXml(m.PlatformMessageId)}\"");
        if (!string.IsNullOrEmpty(m.ReplyToPlatformMessageId))
            attrs.Add($"reply=\"{EscapeXml(m.ReplyToPlatformMessageId)}\"");
        if (IsBotMentionedInMessage(m))
            attrs.Add("mentioned=\"true\"");
        if (!string.IsNullOrEmpty(m.MentionedPlatformIds))
            attrs.Add($"mentioned_users=\"{EscapeXml(m.MentionedPlatformIds)}\"");
        var attrStr = attrs.Count > 0 ? " " + string.Join(" ", attrs) : "";
        var nmsText = $"<new_messages>\n<message role=\"user\" sender=\"{name}\"{attrStr}>\n{EscapeXml(m.Content)}\n</message>\n</new_messages>";

        var nmsMsg = new Message { Role = "user", Content = nmsText };

        // 图片：从 ImageHashes 组装 ContentParts
        if (!string.IsNullOrEmpty(m.ImageHashes))
        {
            var imgPaths = new List<string>();
            var imgHashes = new List<string>();
            foreach (var hash in m.ImageHashes.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmedHash = hash.Trim();
                var p = await ImageStorage.GetModelInputPathAsync(trimmedHash);
                if (!string.IsNullOrEmpty(p)) { imgPaths.Add(p); imgHashes.Add(trimmedHash); }
            }
            if (imgPaths.Count > 0)
            {
                var parts = await BuildContentPartsWithImagePaths(nmsText, imgPaths, imgHashes);
                if (parts.Count > 1) nmsMsg.ContentParts = parts;
            }
        }

        return nmsMsg;
    }

    /// <summary>
    /// 构建滞后图片描述/OCR 补注消息列表。
    /// 对应 ChannelContextBuilder.cs 638-660 滞后补注段。
    /// 遍历 st.RoundImageHashes，补注尚未注入的描述和 OCR。
    /// </summary>
    public async Task<List<Message>> BuildLaggingImageInjectionsAsync(ChannelContextState st)
    {
        var msgs = new List<Message>();

        // 滞后描述补注
        foreach (var hash in st.RoundImageHashes)
        {
            if (st.InjectedDescriptions.Contains(hash)) continue;
            var desc = await ImageStorage.GetDescriptionAsync(hash);
            if (!string.IsNullOrEmpty(desc))
            {
                st.InjectedDescriptions.Add(hash);
                msgs.Add(new Message { Role = "user", Content = $"[图片描述] 之前图片的描述已就绪：{desc}" });
            }
        }

        // 滞后 OCR 补注
        foreach (var hash in st.RoundImageHashes)
        {
            if (st.InjectedOcrTexts.Contains(hash)) continue;
            var ocrInjection = await BuildOcrInjectionTextAsync(hash, true);
            if (!string.IsNullOrEmpty(ocrInjection))
            {
                st.InjectedOcrTexts.Add(hash);
                msgs.Add(new Message { Role = "user", Content = ocrInjection });
            }
        }

        return msgs;
    }
}
