// Plugins/Plugin.NetworkTools/NetworkToolsLoopComponent.cs
using System.Text;
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[Component(Name = "network-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Enabled,
    Review = Applicability.Disabled, SubAgent = Applicability.Enabled)]
public class NetworkToolsLoopComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private string _workspaceDir = "";
    private string _loopId = "";
    private List<DownloadNotification> _pendingNotifications = new();

    private HttpRequestTool? _httpRequest;
    private DownloadFileTool? _downloadFile;
    private ListDownloadsTool? _listDownloads;
    private CancelDownloadTool? _cancelDownload;

    public override ComponentMeta Meta => new()
    {
        Name = "network-tools",
        Description = "网络访问：HTTP请求、文件下载",
        DefaultEnabled = true,
        PromptPriority = 40
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_httpRequest != null) yield return _httpRequest;
            if (_downloadFile != null) yield return _downloadFile;
            if (_listDownloads != null) yield return _listDownloads;
            if (_cancelDownload != null) yield return _cancelDownload;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;
        _loopId = context.LoopId;

        // Workspace路径：从插件存储目录推算
        _workspaceDir = Path.GetFullPath(Path.Combine(context.Storage.InstanceDirectory,
            "..", "..", "..", "Workspace"));
        Directory.CreateDirectory(_workspaceDir);

        var http = NetworkToolsAccessor.HttpClient
            ?? throw new InvalidOperationException("NetworkToolsGlobalComponent 未初始化");
        var security = NetworkToolsAccessor.Security
            ?? throw new InvalidOperationException("SecurityConfig 未加载");
        var store = NetworkToolsAccessor.Store
            ?? throw new InvalidOperationException("DownloadStore 未创建");

        _httpRequest = new HttpRequestTool(http, security);
        _downloadFile = new DownloadFileTool(http, security, store, _workspaceDir, _loopId);
        _listDownloads = new ListDownloadsTool(store, _loopId);
        _cancelDownload = new CancelDownloadTool(store);

        return Task.CompletedTask;
    }

    public override Task OnBeforeInvokeAsync()
    {
        var store = NetworkToolsAccessor.Store;
        if (store != null)
        {
            var notifications = store.DrainNotifications(_loopId);
            _pendingNotifications.AddRange(notifications);
        }
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        if (_pendingNotifications.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("[网络下载通知]");

        foreach (var n in _pendingNotifications)
        {
            if (n.Status == "completed")
                sb.AppendLine($"- 下载完成: {n.FileName} ({FormatSize(n.Size)}) → {n.RelativePath}");
            else if (n.Status == "failed")
                sb.AppendLine($"- 下载失败: {n.FileName}: {n.Error}");
            else if (n.Status == "cancelled")
                sb.AppendLine($"- 下载已取消: {n.FileName}");
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
