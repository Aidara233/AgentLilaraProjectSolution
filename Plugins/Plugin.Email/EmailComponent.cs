using AgentLilara.PluginSDK;
using Plugin.Email.Tools;

namespace Plugin.Email;

[Component(Name = "email", Scope = ComponentScope.Global)]
public class EmailComponent : GlobalComponentBase
{
    private EmailConnectionManager _connMgr = null!;
    private readonly List<ITool> _tools = new();
    private string _workspaceDir = "";

    public override ComponentMeta Meta => new()
    {
        Name = "email",
        Description = "邮件收发工具（SMTP/IMAP，单账户）",
        DefaultEnabled = true,
        PromptPriority = 200
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        var config = EmailConfig.Load(context.Storage.GlobalDirectory);
        _connMgr = new EmailConnectionManager(config);
        _workspaceDir = context.Storage.WorkspaceDirectory;

        _tools.Add(new SendEmailTool(_connMgr, context.Storage));
        _tools.Add(new CheckUnreadTool(_connMgr));
        _tools.Add(new CheckEmailTool(_connMgr));
        _tools.Add(new ReadEmailTool(_connMgr));
        _tools.Add(new SearchEmailTool(_connMgr));
        _tools.Add(new DownloadAttachmentTool(_connMgr, context.Storage));
        _tools.Add(new DeleteEmailTool(_connMgr));
        _tools.Add(new MarkAllReadTool(_connMgr));
        _tools.Add(new ListFoldersTool(_connMgr));

        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection(LoopInfo caller)
    {
        if (_connMgr?.Configured == true)
            return $"[邮件] 已配置 {_connMgr.SmtpUsername}。邮件附件下载/发送使用工作目录相对路径。";
        return null;
    }
}
