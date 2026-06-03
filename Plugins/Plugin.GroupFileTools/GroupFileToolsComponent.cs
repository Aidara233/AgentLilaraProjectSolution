using System.Text;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.GroupFileTools;

[Component(Name = "group-file-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Disabled)]
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

    public override async Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;

        var adapterAccess = context.GetService<IAdapterAccess>();
        var channelAccess = context.GetService<IChannelAccess>();
        if (adapterAccess == null || channelAccess == null) return;

        // 解析 channelId 和 adapterId
        var loopId = context.LoopId;
        var colonIdx = loopId.LastIndexOf(':');
        if (colonIdx < 0 || !int.TryParse(loopId[(colonIdx + 1)..], out var channelId))
            channelId = 0;

        var detail = await channelAccess.GetChannelDetailAsync(channelId);
        if (detail == null || string.IsNullOrEmpty(detail.Name)) return;

        var adapterId = adapterAccess.GetAdapterIdForChannel(detail.Name);
        if (adapterId == null) return;

        // Workspace 路径
        var workspaceDir = Path.GetFullPath(Path.Combine(
            context.Storage.InstanceDirectory, "..", "..", "..", "Workspace"));
        Directory.CreateDirectory(workspaceDir);

        var http = GroupFileToolsAccessor.HttpClient
            ?? throw new InvalidOperationException("GroupFileGlobalComponent 未初始化");
        var store = GroupFileToolsAccessor.Store
            ?? throw new InvalidOperationException("GroupFileDownloadStore 未创建");

        _listFiles = new ListGroupFilesTool(adapterAccess, adapterId);
        _downloadFile = new DownloadGroupFileTool(adapterAccess,
            adapterId, workspaceDir, http, store, loopId);
        _downloadChatFile = new DownloadChatFileTool(adapterAccess,
            adapterId, workspaceDir, store, loopId);
    }

    public override Task OnBeforeInvokeAsync()
    {
        var store = GroupFileToolsAccessor.Store;
        if (store != null)
        {
            var notifications = store.DrainNotifications(_ctx.LoopId);
            _pendingNotifications.AddRange(notifications);
        }
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        var sb = new StringBuilder();

        sb.AppendLine("[群文件] download_group_file / download_chat_file 为异步下载，完成后自动在此通知结果。不要反复检查进度。");

        if (_pendingNotifications.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[群文件下载通知]");
            foreach (var n in _pendingNotifications)
            {
                if (n.Status == "completed")
                    sb.AppendLine($"- 下载完成: {n.FileName} ({FormatSize(n.Size)}) → {n.SavePath}");
                else if (n.Status == "failed")
                    sb.AppendLine($"- 下载失败: {n.FileName}: {n.Error}");
            }
            _pendingNotifications.Clear();
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        > 1_000_000 => $"{bytes / 1_000_000.0:F1} MB",
        > 1_000 => $"{bytes / 1_000.0:F1} KB",
        _ => $"{bytes} B"
    };
}
