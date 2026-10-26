using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Plugin.Email.Tools;

namespace Plugin.Email;

[Component(Name = "email", Scope = ComponentScope.Global)]
public class EmailComponent : GlobalComponentBase
{
    private EmailConnectionManager _connMgr = null!;
    private EmailEventSource? _source;
    private readonly List<ITool> _tools = new();

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

        _tools.Add(new SendEmailTool(_connMgr, context.Workspace));
        _tools.Add(new CheckUnreadTool(_connMgr));
        _tools.Add(new CheckEmailTool(_connMgr));
        _tools.Add(new ReadEmailTool(_connMgr));
        _tools.Add(new SearchEmailTool(_connMgr));
        _tools.Add(new DownloadAttachmentTool(_connMgr, context.Workspace));
        _tools.Add(new DeleteEmailTool(_connMgr));
        _tools.Add(new MarkAllReadTool(_connMgr));
        _tools.Add(new ListFoldersTool(_connMgr));

        // 新邮件作为事件推给框架：模型不必反复调 check_unread 才知道有没有信来。
        // 关掉时只保留工具路径（模型主动查询）。
        if (config.EventSource.Enabled && _connMgr.Configured)
        {
            _source = new EmailEventSource(_connMgr, config.EventSource, context.Storage.GlobalDirectory);
            var registrar = context.GetService<ISourceRegistrar>();

            if (registrar is null)
            {
                // 宿主没有登记面：不静默降级成"看起来配好了其实没人收"
                Console.Error.WriteLine("[email] 宿主未提供 ISourceRegistrar，邮件事件推送未启用");
            }
            else
            {
                registrar.Register(_source);
            }
        }

        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        _source = null;
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection(LoopInfo caller)
    {
        if (_connMgr?.Configured == true)
            return $"[邮件] 已配置 {_connMgr.SmtpUsername}。邮件附件下载/发送使用工作目录相对路径。";
        return null;
    }
}
