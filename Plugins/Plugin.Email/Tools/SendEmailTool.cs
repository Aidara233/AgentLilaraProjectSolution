using MailKit.Net.Smtp;
using MimeKit;
using AgentLilara.PluginSDK;

namespace Plugin.Email.Tools;

[ToolMeta(Group = "email", ContinueLoop = true)]
public class SendEmailTool : ITool
{
    private readonly EmailConnectionManager _connMgr;
    private readonly IPluginStorage _storage;

    public SendEmailTool(EmailConnectionManager connMgr, IPluginStorage storage)
    {
        _connMgr = connMgr;
        _storage = storage;
    }

    public string Name => "send_email";
    public string Description => "发送邮件。收件人可逗号分隔多个。attachments 可选，逗号分隔相对路径（如 报告.pdf,images/photo.png），文件必须在工作目录中。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("to", "收件人地址（可逗号分隔多个）", 0),
        new("subject", "邮件主题", 1),
        new("body", "邮件正文", 2),
        new("attachments", "可选，附件相对路径（逗号分隔，如 报告.pdf, images/photo.png）", 3, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (resolvedInputs.Count < 3 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
            return new ToolResult { Status = "failed", Error = "缺少收件人、主题或正文" };

        var to = resolvedInputs[0].Trim();
        var subject = resolvedInputs[1].Trim();
        var body = resolvedInputs[2].Trim();
        var attachmentsStr = resolvedInputs.Count > 3 ? resolvedInputs[3]?.Trim() : null;

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("", _connMgr.SmtpUsername));

            foreach (var addr in to.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = addr.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    message.To.Add(MailboxAddress.Parse(trimmed));
            }

            message.Subject = subject;

            var builder = new BodyBuilder { TextBody = body };

            if (!string.IsNullOrEmpty(attachmentsStr))
            {
                foreach (var relPath in attachmentsStr.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = relPath.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    var sanitized = trimmed.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var fullPath = Path.GetFullPath(Path.Combine(_storage.WorkspaceDirectory, sanitized));
                    var workspaceRoot = Path.GetFullPath(_storage.WorkspaceDirectory);
                    var workspaceRootWithSep = workspaceRoot.EndsWith(Path.DirectorySeparatorChar)
                        ? workspaceRoot : workspaceRoot + Path.DirectorySeparatorChar;
                    if (!fullPath.StartsWith(workspaceRootWithSep, StringComparison.OrdinalIgnoreCase)
                        && !fullPath.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase))
                        return new ToolResult { Status = "failed", Error = $"路径超出工作区范围: {trimmed}" };

                    if (!File.Exists(fullPath))
                        return new ToolResult { Status = "failed", Error = $"附件文件不存在: {trimmed}" };

                    builder.Attachments.Add(fullPath);
                }
            }

            message.Body = builder.ToMessageBody();

            await _connMgr.ExecuteSmtpAsync(async smtp =>
            {
                await smtp.SendAsync(message, ct);
            }, ct);

            return new ToolResult { Status = "success", Data = $"邮件已发送至 {to}" };
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = $"SMTP 发送失败: {ex.Message}" };
        }
    }
}
