using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[Component(Name = "browser", Scope = ComponentScope.Global)]
public class BrowserComponent : GlobalComponentBase
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private BrowserSessionManager? _sessionManager;
    private BrowserConfig? _config;
    private IPluginStorage? _storage;
    private Timer? _cleanupTimer;

    public override ComponentMeta Meta => new()
    {
        Name = "browser",
        Description = "Playwright 浏览器自动化组件（Chromium）",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => Enumerable.Empty<ITool>();

    public override async Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _storage = context.Storage;

        // 加载配置
        var configPath = Path.Combine(_storage.GlobalDirectory, "..", "..", "Network", "BrowserConfig.json");
        _config = BrowserConfig.Load(configPath);

        // 解析浏览器路径
        var browserPath = ResolveBrowserPath();
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browserPath);

        // 启动 Playwright 和浏览器
        Console.WriteLine("[Browser] 正在启动 Playwright...");
        _playwright = await Playwright.CreateAsync();

        Console.WriteLine("[Browser] 正在启动 Chromium...");
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = _config.HeadlessMode,
            Timeout = _config.DefaultTimeout,
            SlowMo = _config.SlowMo
        });

        // 创建会话管理器
        _sessionManager = new BrowserSessionManager(_browser, _config);

        // 启动定时清理任务（每5分钟）
        _cleanupTimer = new Timer(async _ =>
        {
            if (_sessionManager != null)
                await _sessionManager.CleanupIdleSessionsAsync();
        }, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

        Console.WriteLine("[Browser] Playwright 浏览器组件已启动");
    }

    private string ResolveBrowserPath()
    {
        if (_storage == null || _config == null)
            throw new InvalidOperationException("组件未初始化");

        // 1. 优先使用插件存储区（生产环境）
        var storageDir = Path.Combine(_storage.GlobalDirectory, "Browsers");
        if (Directory.Exists(Path.Combine(storageDir, "chromium-1097")))
        {
            Console.WriteLine($"[Browser] 使用插件存储区浏览器: {storageDir}");
            return storageDir;
        }

        // 2. 降级到配置文件路径（开发环境）
        if (Directory.Exists(Path.Combine(_config.FallbackBrowserPath, "chromium-1097")))
        {
            Console.WriteLine($"[Browser] 使用配置路径浏览器: {_config.FallbackBrowserPath}");
            return _config.FallbackBrowserPath;
        }

        throw new InvalidOperationException(
            "未找到 Chromium 浏览器。请确认:\n" +
            $"1. 插件存储区: {storageDir}\n" +
            $"2. 配置路径: {_config.FallbackBrowserPath}\n" +
            "请运行 PlaywrightDemo/install-browser.ps1 安装浏览器。"
        );
    }

    public override async Task OnShutdownAsync(ShutdownReason reason)
    {
        Console.WriteLine("[Browser] 正在关闭 Playwright...");

        _cleanupTimer?.Dispose();

        if (_sessionManager != null)
            await _sessionManager.DisposeAllAsync();

        if (_browser != null)
            await _browser.CloseAsync();

        _playwright?.Dispose();

        Console.WriteLine("[Browser] Playwright 浏览器组件已关闭");
    }

    public override string? BuildPromptSection(LoopInfo caller)
    {
        if (_sessionManager != null)
            return "[Browser] Playwright Chromium 浏览器可用。";
        return null;
    }

    public BrowserSessionManager? GetSessionManager() => _sessionManager;
    public BrowserConfig? GetConfig() => _config;
    public IPluginStorage? GetStorage() => _storage;
}
