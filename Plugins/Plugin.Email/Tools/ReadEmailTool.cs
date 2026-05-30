using MailKit;
using AgentLilara.PluginSDK;
using MimeKit;

namespace Plugin.Email.Tools;

[ToolMeta(Group = "email", ContinueLoop = true)]
public class ReadEmailTool : ITool
{
    private readonly EmailConnectionManager _connMgr;

    public ReadEmailTool(EmailConnectionManager connMgr)
    {
        _connMgr = connMgr;
    }

    public string Name => "read_email";
    public string Description => "阅读邮件完整内容（标记已读），内联图片直接返回。";
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
                await imap.Inbox.OpenAsync(FolderAccess.ReadWrite, ct);
                var message = await imap.Inbox.GetMessageAsync(uid, ct);

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"发件人: {message.From}");
                sb.AppendLine($"收件人: {message.To}");
                sb.AppendLine($"主题: {message.Subject}");
                sb.AppendLine($"日期: {message.Date.ToString("yyyy-MM-dd HH:mm:ss")}");
                sb.AppendLine();

                // 正文
                var textBody = message.TextBody ?? message.HtmlBody;
                if (!string.IsNullOrEmpty(textBody))
                {
                    // 简单清理 HTML 标签
                    var clean = StripHtml(textBody);
                    sb.AppendLine(clean);
                }
                else
                {
                    sb.AppendLine("(无文本内容)");
                }

                // 附件列表
                var attachments = message.Attachments.ToList();
                if (attachments.Count > 0)
                {
                    sb.AppendLine($"\n附件 ({attachments.Count}):");
                    foreach (var att in attachments)
                    {
                        var name = att.ContentDisposition?.FileName ?? att.ContentType.Name ?? "unknown";
                        long size = 0;
                        if (att is MimePart mp)
                            size = mp.Content.Stream?.Length ?? 0;
                        sb.AppendLine($"  - {name} ({FormatSize(size)})");
                    }
                }

                var toolResult = new ToolResult { Status = "success", Data = sb.ToString() };

                // 内联图片作为附件返回
                var inlineImages = message.BodyParts
                    .Where(p => p is MimePart mp && !mp.IsAttachment && p.ContentType.IsMimeType("image", "*"))
                    .Cast<MimePart>()
                    .ToList();

                if (inlineImages.Count > 0)
                {
                    toolResult.Attachments = new List<ContentAttachment>();
                    foreach (var img in inlineImages)
                    {
                        using var ms = new MemoryStream();
                        await img.Content.DecodeToAsync(ms, ct);
                        toolResult.Attachments.Add(new ContentAttachment
                        {
                            Type = "image",
                            Base64Data = Convert.ToBase64String(ms.ToArray()),
                            MediaType = img.ContentType.MimeType
                        });
                    }
                }

                return toolResult;
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = ex.Message };
        }
    }

    private static string StripHtml(string html)
    {
        // 简单 HTML 清理，保留可读性
        var noTags = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", "\n");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        // 压缩多余空行
        return System.Text.RegularExpressions.Regex.Replace(decoded, @"\n\s*\n", "\n\n").Trim();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
