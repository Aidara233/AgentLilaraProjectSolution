using MailKit;
using MailKit.Net.Imap;
using AgentLilara.PluginSDK;

namespace Plugin.Email.Tools;

[ToolMeta(Group = "email", ContinueLoop = true)]
public class CheckUnreadTool : ITool
{
    private readonly EmailConnectionManager _connMgr;

    public CheckUnreadTool(EmailConnectionManager connMgr)
    {
        _connMgr = connMgr;
    }

    public string Name => "check_unread";
    public string Description => "列出未读邮件摘要（不标记已读）。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("skip", "跳过前 N 封（偏移），默认 0", 0, isRequired: false),
        new("limit", "最多显示 N 封，默认 10", 1, isRequired: false),
        new("folder", "邮箱文件夹，默认 INBOX", 2, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var skip = ParseInt(resolvedInputs, 0, 0);
        var limit = ParseInt(resolvedInputs, 1, 10);
        var folder = GetInput(resolvedInputs, 2, "INBOX");

        try
        {
            var result = await _connMgr.ExecuteImapAsync(async imap =>
            {
                var mailFolder = await GetFolderAsync(imap, folder, FolderAccess.ReadOnly, ct);
                var uids = await mailFolder.SearchAsync(MailKit.Search.SearchQuery.NotSeen, ct);
                var totalUnread = uids.Count;

                var sorted = uids.OrderByDescending(u => u.Id).Skip(skip).Take(limit).ToList();
                if (sorted.Count == 0)
                    return new ToolResult { Status = "success", Data = "未读邮件: 0 封" };

                var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags;
                var messages = new List<IMessageSummary>();
                foreach (var uid in sorted)
                    messages.AddRange(await mailFolder.FetchAsync(new[] { uid }, items, ct));

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"未读邮件 (共 {totalUnread} 封，显示 {skip + 1}-{skip + sorted.Count}):");
                foreach (var msg in messages)
                {
                    var subject = msg.Envelope?.Subject ?? "(无主题)";
                    var from = msg.Envelope?.From?.ToString() ?? "(未知)";
                    var date = msg.Envelope?.Date.HasValue == true ? msg.Envelope.Date.Value.ToString("yyyy-MM-dd HH:mm") : "";
                    sb.AppendLine($"  #{msg.UniqueId} | {from} | {subject} | {date}");
                }
                return new ToolResult { Status = "success", Data = sb.ToString() };
            }, ct);

            return result;
        }
        catch (Exception ex)
        {
            return new ToolResult { Status = "failed", Error = ex.Message };
        }
    }

    private static int ParseInt(List<string> inputs, int index, int defaultValue)
    {
        if (inputs.Count <= index || string.IsNullOrWhiteSpace(inputs[index]))
            return defaultValue;
        return int.TryParse(inputs[index].Trim(), out var v) ? v : defaultValue;
    }

    private static string GetInput(List<string> inputs, int index, string defaultValue)
    {
        if (inputs.Count <= index || string.IsNullOrWhiteSpace(inputs[index]))
            return defaultValue;
        return inputs[index].Trim();
    }

    private static async Task<IMailFolder> GetFolderAsync(ImapClient imap, string folder, FolderAccess access, CancellationToken ct)
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
