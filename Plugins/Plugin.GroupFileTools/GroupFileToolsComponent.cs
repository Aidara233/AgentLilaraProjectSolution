using System.Text;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.GroupFileTools;

[Component(Name = "group-file-tools", Scope = ComponentScope.Loop)]
public class GroupFileToolsComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private ListGroupFilesTool? _listFiles;
    private DownloadGroupFileTool? _downloadFile;
    private DownloadChatFileTool? _downloadChatFile;
    private List<GroupFileDownloadNotification> _pendingNotifications = new();

    public override ComponentMeta Meta => new()
    {
        Name = "group-file-tools",
        Description = "文件下载管理（list_group_files, download_group_file, download_chat_file），群聊私聊均可用",
        DefaultEnabled = true,
        PromptPriority = 60
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_listFiles != null) yield return _listFiles;
            if (_downloadFile != null) yield return _downloadFile;
            if (_downloadChatFile != null) yield return _downloadChatFile;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;

        var adapterAccess = context.GetService<IAdapterAccess>();
        var channelAccess = context.GetService<IChannelAccess>();
        // 连接驱动：频道号由 IConnectedChannel 运行期决定。组件装配早于 AutoClaim，
        // 所以这里只拿引用、不解析频道（否则会锁定 channelId=0 导致适配器不存在、工具直接不注册）。
        var connected = context.GetService<IConnectedChannel>();
        if (adapterAccess == null || channelAccess == null) return Task.CompletedTask;

        // Workspace 访问
        var workspace = context.Workspace;

        var http = GroupFileToolsAccessor.HttpClient
            ?? throw new InvalidOperationException("GroupFileGlobalComponent 未初始化");
        var store = GroupFileToolsAccessor.Store
            ?? throw new InvalidOperationException("GroupFileDownloadStore 未创建");

        _listFiles = new ListGroupFilesTool(adapterAccess, channelAccess, connected);
        _downloadFile = new DownloadGroupFileTool(adapterAccess, channelAccess,
            connected, workspace, http, store, context.SessionId);
        _downloadChatFile = new DownloadChatFileTool(adapterAccess,
            connected, workspace, store, context.SessionId);
        return Task.CompletedTask;
    }

    public override Task OnBeforeInvokeAsync()
    {
        var store = GroupFileToolsAccessor.Store;
        if (store != null)
        {
            var notifications = store.DrainNotifications(_ctx.SessionId);
            _pendingNotifications.AddRange(notifications);
        }
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        if (_pendingNotifications.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("[群文件下载通知]");
        foreach (var n in _pendingNotifications)
        {
            if (n.Status == "completed")
                sb.AppendLine($"- 下载完成: {n.FileName} ({FormatSize(n.Size)}) → {n.SavePath}");
            else if (n.Status == "failed")
                sb.AppendLine($"- 下载失败: {n.FileName}: {n.Error}");
        }
        _pendingNotifications.Clear();

        return sb.ToString().TrimEnd();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        > 1_000_000 => $"{bytes / 1_000_000.0:F1} MB",
        > 1_000 => $"{bytes / 1_000.0:F1} KB",
        _ => $"{bytes} B"
    };
}
