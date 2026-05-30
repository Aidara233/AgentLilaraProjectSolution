using MailKit;
using AgentLilara.PluginSDK;

namespace Plugin.Email.Tools;

[ToolMeta(Group = "email", ContinueLoop = true)]
public class DeleteEmailTool : ITool
{
    private readonly EmailConnectionManager _connMgr;

    public DeleteEmailTool(EmailConnectionManager connMgr)
    {
        _connMgr = connMgr;
    }

    public string Name => "delete_email";
    public string Description => "删除指定邮件。";
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
                await imap.Inbox.AddFlagsAsync([uid], MessageFlags.Deleted, true, ct);
                await imap.Inbox.ExpungeAsync(ct);
                return new ToolResult { Status = "success", Data = $"邮件 {uidVal} 已删除" };
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = ex.Message };
        }
    }
}
