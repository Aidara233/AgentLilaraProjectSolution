// Plugins/Plugin.NetworkTools/NetworkToolsGlobalComponent.cs
using System.Net;
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[Component(Name = "network-tools-global", Scope = ComponentScope.Global)]
public class NetworkToolsGlobalComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private SecurityConfig _security = null!;
    private HttpClient _http = null!;
    private DownloadStore? _store;

    public override ComponentMeta Meta => new()
    {
        Name = "network-tools-global",
        Description = "网络访问全局组件：HttpClient管理 + 后台下载",
        DefaultEnabled = true,
        PromptPriority = 99
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;

        // 加载安全配置
        var configDir = Path.Combine(context.Storage.GlobalDirectory, "..");
        _security = SecurityConfig.Load(Path.GetFullPath(configDir));

        // 初始化 DownloadStore 单例
        _store = new DownloadStore { MaxConcurrent = _security.MaxConcurrentDownloads };

        // 初始化 HttpClient（自动跟随重定向，初始URL校验提供主要防护）
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = _security.MaxRedirects,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(_security.DefaultTimeoutSeconds)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(_security.UserAgent);

        // 注册唤醒回调：Global收到下载完成信号 → WakeLoop
        NetworkToolsNotifier.OnDownloadCompleted = loopId =>
        {
            _ctx.WakeLoop(loopId);
        };

        // 暴露静态引用供Loop组件工具使用
        NetworkToolsAccessor.Configure(_http, _security, _store);

        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        NetworkToolsNotifier.OnDownloadCompleted = null;
        _store?.Shutdown();
        _http?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// 静态访问器：Global组件初始化后设置，Loop组件工具通过此访问共享资源。
/// </summary>
public static class NetworkToolsAccessor
{
    public static HttpClient? HttpClient { get; private set; }
    public static SecurityConfig? Security { get; private set; }
    public static DownloadStore? Store { get; private set; }

    public static void Configure(HttpClient http, SecurityConfig security, DownloadStore store)
    {
        HttpClient = http;
        Security = security;
        Store = store;
    }
}
