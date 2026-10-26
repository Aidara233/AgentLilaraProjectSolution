using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Plugin.Email.Tools;

namespace Plugin.Email;

[Component(Name = "email", Scope = ComponentScope.Global)]
public class EmailComponent : GlobalComponentBase
{
    private EmailConnectionManager _connMgr = null!;
    private EmailEventSource? _source;
    private ISourceRegistrar? _registrar;
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
            _source = new EmailEventSource(
                new ImapMailboxSessionFactory(_connMgr, config.EventSource.Folder),
                config.EventSource,
                context.Storage.GlobalDirectory);
            _registrar = context.GetService<ISourceRegistrar>();

            if (_registrar is null)
            {
                // 宿主没有登记面：不静默降级成"看起来配好了其实没人收"
                Console.Error.WriteLine("[email] 宿主未提供 ISourceRegistrar，邮件事件推送未启用");
            }
            else
            {
                _registrar.Register(_source);
            }
        }

        return Task.CompletedTask;
    }

    public override async Task OnDisabledAsync()
    {
        // 组件被禁用还要继续轮询邮箱，是把用户关掉的开关当成没关。
        if (_registrar is not null && _source is not null)
        {
            await _source.StopAsync();
            _registrar.Unregister(_source.Id);
        }
    }

    public override Task OnEnabledAsync()
    {
        if (_registrar is not null && _source is not null)
        {
            // StartAsync 是幂等的：宿主启动时已拉起、或这里重新拉起都成立
            _ = _source.StartAsync();
        }

        return Task.CompletedTask;
    }

    public override async Task OnShutdownAsync(ShutdownReason reason)
    {
        if (_source is not null)
        {
            await _source.StopAsync();
        }

        _source = null;
        _registrar = null;
    }

    public override string? BuildPromptSection(LoopInfo caller)
    {
        if (_connMgr?.Configured == true)
            return $"[邮件] 已配置 {_connMgr.SmtpUsername}。邮件附件下载/发送使用工作目录相对路径。";
        return null;
    }
}
