// Plugins/Plugin.HomeAssistant/HaGlobalComponent.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Logging;
using AgentLilara.PluginSDK.Services;

namespace Plugin.HomeAssistant;

/// <summary>
/// Home Assistant 全局组件：共享 HttpClient + 连接配置。
/// 不持有后台任务（HA 调用都是同步 HTTP），故不实现 ISessionResource。
/// 工具经 HaToolsAccessor 静态桥接访问（Static Accessor Bridge 模式）。
/// </summary>
[Component(Name = "ha-global", Scope = ComponentScope.Global)]
public class HaGlobalComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private HttpClient _http = null!;
    private HaConfig _config = null!;
    private ISignalLogger? _log;

    public override ComponentMeta Meta => new()
    {
        Name = "ha-global",
        Description = "Home Assistant 连接管理：共享 HttpClient + 连接配置",
        DefaultEnabled = true,
        PromptPriority = 99
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;
        _log = context.GetService<ISignalLogger>();

        _config = HaConfig.Load(context.Storage);

        // 按需连接：不在启动时 ping/认证，首次工具调用时校验 token/可达性
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
        };
        _http.DefaultRequestHeaders.Authorization = new("Bearer", _config.Token);

        HaToolsAccessor.Configure(this);

        if (string.IsNullOrEmpty(_config.Token))
            _log?.Warn("ha", "config-token-missing", new { path = "Storage/PluginData/ha/HaConfig.json" });

        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        _http?.Dispose();
        HaToolsAccessor.Clear();
        return Task.CompletedTask;
    }

    public HttpClient Http => _http;
    public HaConfig Config => _config;

    /// <summary>
    /// 校验配置是否可用：BaseUrl 可构造 + token 非空。
    /// 返回 null = 可用；否则返回不可用原因。
    /// </summary>
    public string? ValidateReady()
    {
        if (string.IsNullOrEmpty(_config.Token))
            return "HA token 未配置（Storage/PluginData/ha/HaConfig.json）";
        if (string.IsNullOrEmpty(_config.Host))
            return "HA Host 未配置";
        return null;
    }
}

/// <summary>静态访问器：Global 组件初始化后设置，Loop 组件工具通过此访问共享 HttpClient 与配置。</summary>
public static class HaToolsAccessor
{
    public static HaGlobalComponent? Global { get; private set; }
    public static void Configure(HaGlobalComponent global) => Global = global;
    public static void Clear() => Global = null;
}