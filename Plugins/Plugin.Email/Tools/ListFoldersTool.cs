using MailKit;
using AgentLilara.PluginSDK;

namespace Plugin.Email.Tools;

[ToolMeta(Group = "email", ContinueLoop = true)]
public class ListFoldersTool : ITool
{
    private readonly EmailConnectionManager _connMgr;

    public ListFoldersTool(EmailConnectionManager connMgr)
    {
        _connMgr = connMgr;
    }

    public string Name => "list_folders";
    public string Description => "列出所有可用邮箱文件夹。";
    public IReadOnlyList<ToolParameter> Parameters => [];
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        try
        {
            var result = await _connMgr.ExecuteImapAsync(async imap =>
            {
                var folders = await imap.GetFoldersAsync(imap.PersonalNamespaces[0], false, ct);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("可用邮箱文件夹:");
                foreach (var folder in folders)
                {
                    var attrs = string.Join(", ", folder.Attributes);
                    sb.AppendLine($"  {folder.FullName} [{attrs}]");
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
}
