using MailKit;
using AgentLilara.PluginSDK;

namespace Plugin.Email.Tools;

[ToolMeta(Group = "email", ContinueLoop = true)]
public class SearchEmailTool : ITool
{
    private readonly EmailConnectionManager _connMgr;

    public SearchEmailTool(EmailConnectionManager connMgr)
    {
        _connMgr = connMgr;
    }

    public string Name => "search_email";
    public string Description => "搜索邮件。query 为空时按时间倒序列出所有邮件。* 标记表示未读。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("query", "搜索关键词（空=列出全部），匹配主题/正文/发件人", 0, isRequired: false),
        new("skip", "跳过前 N 封，默认 0", 1, isRequired: false),
        new("limit", "最多显示 N 封，默认 10", 2, isRequired: false),
        new("folder", "邮箱文件夹，默认 INBOX", 3, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var query = GetInput(resolvedInputs, 0, "");
        var skip = ParseInt(resolvedInputs, 1, 0);
        var limit = ParseInt(resolvedInputs, 2, 10);
        var folder = GetInput(resolvedInputs, 3, "INBOX");

        try
        {
            var result = await _connMgr.ExecuteImapAsync(async imap =>
            {
                var mailFolder = await GetFolderAsync(imap, folder, FolderAccess.ReadOnly, ct);

                MailKit.Search.SearchQuery search;
                if (string.IsNullOrWhiteSpace(query))
                {
                    search = MailKit.Search.SearchQuery.All;
                }
                else
                {
                    search = MailKit.Search.SearchQuery.Or(
                        MailKit.Search.SearchQuery.SubjectContains(query),
                        MailKit.Search.SearchQuery.Or(
                            MailKit.Search.SearchQuery.BodyContains(query),
                            MailKit.Search.SearchQuery.FromContains(query)
                        )
                    );
                }

                var uids = await mailFolder.SearchAsync(search, ct);
                var total = uids.Count;
                var sorted = uids.OrderByDescending(u => u.Id).Skip(skip).Take(limit).ToList();

                if (sorted.Count == 0)
                    return new ToolResult { Status = "success", Data = $"搜索结果: 0 封" };

                var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags;
                var messages = new List<IMessageSummary>();
                foreach (var uid in sorted)
                    messages.AddRange(await mailFolder.FetchAsync(new[] { uid }, items, ct));

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"搜索结果 (共 {total} 封，显示 {skip + 1}-{skip + sorted.Count}):");
                foreach (var msg in messages)
                {
                    var seen = (msg.Flags & MessageFlags.Seen) != 0;
                    var flag = seen ? " " : "*";
                    var subject = msg.Envelope?.Subject ?? "(无主题)";
                    var from = msg.Envelope?.From?.ToString() ?? "(未知)";
                    var date = msg.Envelope?.Date.HasValue == true ? msg.Envelope.Date.Value.ToString("yyyy-MM-dd HH:mm") : "";
                    sb.AppendLine($"  {flag} #{msg.UniqueId} | {subject} | {from} | {date}");
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
