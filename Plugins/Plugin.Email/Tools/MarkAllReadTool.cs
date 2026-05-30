using MailKit;
using AgentLilara.PluginSDK;

namespace Plugin.Email.Tools;

[ToolMeta(Group = "email", ContinueLoop = true)]
public class MarkAllReadTool : ITool
{
    private readonly EmailConnectionManager _connMgr;

    public MarkAllReadTool(EmailConnectionManager connMgr)
    {
        _connMgr = connMgr;
    }

    public string Name => "mark_all_read";
    public string Description => "一键标记文件夹中所有邮件为已读。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("folder", "邮箱文件夹，默认 INBOX", 0, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var folder = resolvedInputs.Count > 0 && !string.IsNullOrWhiteSpace(resolvedInputs[0])
            ? resolvedInputs[0].Trim() : "INBOX";

        try
        {
            var result = await _connMgr.ExecuteImapAsync(async imap =>
            {
                var mailFolder = await GetFolderAsync(imap, folder, FolderAccess.ReadWrite, ct);
                var unseen = await mailFolder.SearchAsync(MailKit.Search.SearchQuery.NotSeen, ct);

                if (unseen.Count == 0)
                    return new ToolResult { Status = "success", Data = "没有未读邮件" };

                await mailFolder.AddFlagsAsync(unseen, MessageFlags.Seen, true, ct);
                return new ToolResult { Status = "success", Data = $"已标记 {unseen.Count} 封邮件为已读" };
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = ex.Message };
        }
    }

    private static async Task<IMailFolder> GetFolderAsync(MailKit.Net.Imap.ImapClient imap, string folder, FolderAccess access, CancellationToken ct)
    {
        if (string.Equals(folder, "INBOX", StringComparison.OrdinalIgnoreCase))
        {
            await imap.Inbox.OpenAsync(access, ct);
            return imap.Inbox;
        }
        var mailFolder = await imap.GetFolderAsync(folder, ct);
        if (mailFolder == null)
            throw new InvalidOperationException($"邮箱文件夹不存在: {folder}");
        await mailFolder.OpenAsync(access, ct);
        return mailFolder;
    }
}
