// Plugins/Plugin.GroupFileTools/GroupFileGlobalComponent.cs
using AgentLilara.PluginSDK;

namespace Plugin.GroupFileTools;

[Component(Name = "group-file-tools-global", Scope = ComponentScope.Global)]
public class GroupFileGlobalComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private HttpClient _http = null!;
    private GroupFileDownloadStore _store = null!;

    public override ComponentMeta Meta => new()
    {
        Name = "group-file-tools-global",
        Description = "群文件下载全局组件：HttpClient 管理 + 下载注册表",
        DefaultEnabled = true,
        PromptPriority = 99
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentLilara/1.0");

        _store = new GroupFileDownloadStore();

        GroupFileNotifier.OnDownloadCompleted = loopId =>
        {
            _ctx.WakeLoop(loopId);
        };

        GroupFileToolsAccessor.Configure(_http, _store);

        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        GroupFileNotifier.OnDownloadCompleted = null;
        GroupFileToolsAccessor.Clear();
        _http?.Dispose();
        return Task.CompletedTask;
    }
}
