using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.WebSearch;

[Component(Name = "web-search-global", Scope = ComponentScope.Global)]
public class WebSearchGlobalComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private HttpClient _http = null!;

    public override ComponentMeta Meta => new()
    {
        Name = "web-search-global",
        Description = "网页搜索全局组件：HttpClient + 搜索后端管理",
        DefaultEnabled = true,
        PromptPriority = 99
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;

        // 加载配置
        var configDir = Path.Combine(context.Storage.GlobalDirectory, "..");
        var config = WebSearchConfig.Load(Path.GetFullPath(configDir));

        // 创建 HttpClient
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentLilara-WebSearch/1.0");

        // 根据配置选择后端
        ISearchBackend backend = config.Backend.ToLowerInvariant() switch
        {
            "tavily" => new TavilySearchBackend(_http, config.Tavily),
            _ => throw new Exception($"未知搜索后端: {config.Backend}")
        };

        WebSearchAccessor.Configure(_http, backend);

        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        _http?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// 静态访问器：Global组件初始化后设置，Loop组件工具通过此访问共享资源。
/// </summary>
public static class WebSearchAccessor
{
    public static HttpClient? HttpClient { get; private set; }
    public static ISearchBackend? Backend { get; private set; }

    public static void Configure(HttpClient http, ISearchBackend backend)
    {
        HttpClient = http;
        Backend = backend;
    }
}
