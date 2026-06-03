using MailKit;
using MimeKit;
using AgentLilara.PluginSDK;

namespace Plugin.Email.Tools;

[ToolMeta(Group = "email", ContinueLoop = true)]
public class DownloadAttachmentTool : ITool
{
    private readonly EmailConnectionManager _connMgr;
    private readonly IPluginStorage _storage;

    public DownloadAttachmentTool(EmailConnectionManager connMgr, IPluginStorage storage)
    {
        _connMgr = connMgr;
        _storage = storage;
    }

    public string Name => "download_attachment";
    public string Description => "下载邮件指定附件到 Workspace。attachment_name 需与 check_email/read_email 显示的文件名完全一致。"
        + " dest_path 为相对路径（如 报告.pdf、images/photo.png），文件将保存到工作目录。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("uid", "邮件 UID", 0),
        new("attachment_name", "附件文件名（需与 check_email 显示的名称完全一致）", 1),
        new("dest_path", "目标相对路径（如 报告.pdf、images/photo.png），保存到工作目录", 2)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (resolvedInputs.Count < 3 || !uint.TryParse(resolvedInputs[0].Trim(), out var uidVal))
            return new ToolResult { Status = "failed", Error = "缺少 UID、附件名或目标路径" };

        var uid = new MailKit.UniqueId(uidVal);
        var attachmentName = resolvedInputs[1].Trim();
        var destPath = resolvedInputs[2].Trim();

        // 沙箱校验
        var sanitized = destPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(_storage.WorkspaceDirectory, sanitized));
        var workspaceRoot = Path.GetFullPath(_storage.WorkspaceDirectory);
        var workspaceRootWithSep = workspaceRoot.EndsWith(Path.DirectorySeparatorChar)
            ? workspaceRoot : workspaceRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(workspaceRootWithSep, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(workspaceRoot, StringComparison.OrdinalIgnoreCase))
            return new ToolResult { Status = "failed", Error = "路径超出工作区范围" };

        // 确保父目录存在
        var parentDir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parentDir))
            Directory.CreateDirectory(parentDir);

        try
        {
            var result = await _connMgr.ExecuteImapAsync(async imap =>
            {
                await imap.Inbox.OpenAsync(FolderAccess.ReadOnly, ct);
                var message = await imap.Inbox.GetMessageAsync(uid, ct);

                var attachment = message.Attachments
                    .FirstOrDefault(a => (a.ContentDisposition?.FileName ?? a.ContentType.Name ?? "")
                        .Equals(attachmentName, StringComparison.OrdinalIgnoreCase));

                if (attachment == null)
                    return new ToolResult { Status = "failed", Error = $"未找到附件: {attachmentName}" };

                await using var fs = File.Create(fullPath);
                if (attachment is MimePart mimePart)
                {
                    if (mimePart.Content != null)
                        await mimePart.Content.DecodeToAsync(fs, ct);
                }
                else if (attachment is MessagePart messagePart)
                {
                    if (messagePart.Message != null)
                        await messagePart.Message.WriteToAsync(fs, ct);
                }

                return new ToolResult { Status = "success", Data = $"附件 {attachmentName} 已保存到工作目录: {destPath}" };
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = ex.Message };
        }
    }
}
