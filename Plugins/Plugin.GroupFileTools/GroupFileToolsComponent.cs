using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.GroupFileTools;

[Component(Name = "group-file-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Disabled)]
public class GroupFileToolsComponent : LoopComponentBase
{
    private ListGroupFilesTool? _listFiles;
    private DownloadGroupFileTool? _downloadFile;

    public override ComponentMeta Meta => new()
    {
        Name = "group-file-tools",
        Description = "群文件管理（list_group_files, download_group_file）",
        DefaultEnabled = true,
        PromptPriority = 60
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_listFiles != null) yield return _listFiles;
            if (_downloadFile != null) yield return _downloadFile;
        }
    }

    public override async Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        var adapterAccess = context.GetService<IAdapterAccess>();
        var channelAccess = context.GetService<IChannelAccess>();
        if (adapterAccess == null || channelAccess == null) return;

        // 解析 channelId 和 adapterId
        var loopId = context.LoopId;
        var colonIdx = loopId.LastIndexOf(':');
        if (colonIdx < 0 || !int.TryParse(loopId[(colonIdx + 1)..], out var channelId))
            channelId = 0;

        var detail = await channelAccess.GetChannelDetailAsync(channelId);
        if (detail == null) return;

        var adapterId = adapterAccess.GetAdapterIdForChannel(detail.PlatformChannelId);
        if (adapterId == null) return;

        // Workspace 路径
        var workspaceDir = Path.GetFullPath(Path.Combine(
            context.Storage.InstanceDirectory, "..", "..", "..", "Workspace"));
        Directory.CreateDirectory(workspaceDir);

        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentLilara/1.0");

        _listFiles = new ListGroupFilesTool(adapterAccess, adapterId);
        _downloadFile = new DownloadGroupFileTool(adapterAccess, channelAccess,
            adapterId, channelId, workspaceDir, http);
    }
}
