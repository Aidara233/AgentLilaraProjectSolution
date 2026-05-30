using MailKit;
using MailKit.Net.Imap;
using AgentLilara.PluginSDK;

namespace Plugin.Email.Tools;

[ToolMeta(Group = "email", ContinueLoop = true)]
public class CheckEmailTool : ITool
{
    private readonly EmailConnectionManager _connMgr;

    public CheckEmailTool(EmailConnectionManager connMgr)
    {
        _connMgr = connMgr;
    }

    public string Name => "check_email";
    public string Description => "查看邮件摘要（不标记已读），含发件人/主题/日期/附件列表。使用 read_email 查看完整内容。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("uid", "邮件 UID", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (resolvedInputs.Count < 1 || !uint.TryParse(resolvedInputs[0].Trim(), out var uidVal))
            return new ToolResult { Status = "failed", Error = "无效的邮件 UID" };

        var uid = new MailKit.UniqueId(uidVal);

        try
        {
            var result = await _connMgr.ExecuteImapAsync(async imap =>
            {
                await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
                var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope |
                    MessageSummaryItems.Flags | MessageSummaryItems.BodyStructure;
                var messages = await imap.Inbox.FetchAsync(new[] { uid }, items, ct);

                if (messages.Count == 0)
                    return new ToolResult { Status = "failed", Error = $"邮件不存在 (UID={uidVal})" };

                var msg = messages[0];
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"邮件 #{msg.UniqueId}");
                sb.AppendLine($"发件人: {(msg.Envelope?.From?.ToString() ?? "(未知)")}");
                sb.AppendLine($"收件人: {(msg.Envelope?.To?.ToString() ?? "(未知)")}");
                sb.AppendLine($"主题: {msg.Envelope?.Subject ?? "(无主题)"}");
                sb.AppendLine($"日期: {(msg.Envelope?.Date.HasValue == true ? msg.Envelope.Date.Value.ToString("yyyy-MM-dd HH:mm:ss") : "")}");

                var seen = (msg.Flags & MessageFlags.Seen) != 0;
                sb.AppendLine($"状态: {(seen ? "已读" : "未读")}");

                // 附件元数据
                var attachments = ExtractAttachmentInfo(msg.Body);
                if (attachments.Count > 0)
                {
                    sb.AppendLine($"\n附件 ({attachments.Count}):");
                    foreach (var a in attachments)
                        sb.AppendLine($"  - {a.Name} ({FormatSize(a.Size)})");
                }

                // 正文统计
                var (textLen, imageCount) = EstimateBody(msg.Body);
                sb.AppendLine($"\n正文: 共约 {textLen} 字，{imageCount} 张内联图片");
                sb.AppendLine("\n[提示] 使用 read_email 查看完整内容（将标记为已读）");

                return new ToolResult { Status = "success", Data = sb.ToString() };
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = ex.Message };
        }
    }

    private record AttachmentInfo(string Name, long Size);

    private static List<AttachmentInfo> ExtractAttachmentInfo(BodyPart? body)
    {
        var list = new List<AttachmentInfo>();
        if (body == null) return list;
        WalkBody(body, list);
        return list;
    }

    private static void WalkBody(BodyPart part, List<AttachmentInfo> list)
    {
        if (part is BodyPartMultipart multipart)
        {
            foreach (var child in multipart.BodyParts)
                WalkBody(child, list);
        }
        else if (part is BodyPartBasic basic)
        {
            if (basic.IsAttachment)
            {
                var name = basic.FileName ?? basic.ContentType.Name ?? "unknown";
                list.Add(new AttachmentInfo(name, (long)basic.Octets));
            }
        }
    }

    private static (int textLen, int imageCount) EstimateBody(BodyPart? body)
    {
        int textLen = 0, imageCount = 0;
        if (body == null) return (0, 0);
        EstimateWalk(body, ref textLen, ref imageCount);
        return (textLen, imageCount);
    }

    private static void EstimateWalk(BodyPart part, ref int textLen, ref int imageCount)
    {
        if (part is BodyPartMultipart multipart)
        {
            foreach (var child in multipart.BodyParts)
                EstimateWalk(child, ref textLen, ref imageCount);
        }
        else if (part is BodyPartBasic basic)
        {
            if (!basic.IsAttachment)
            {
                if (basic.ContentType.IsMimeType("text", "*"))
                    textLen += (int)basic.Octets;
                else if (basic.ContentType.IsMimeType("image", "*"))
                    imageCount++;
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
