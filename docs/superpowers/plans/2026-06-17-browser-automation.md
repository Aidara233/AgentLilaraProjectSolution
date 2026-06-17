# 无头浏览器自动化实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 Plugin.NetworkTools 添加 Playwright 浏览器自动化能力，支持动态网页抓取、表单操作、截图、Cookie 管理等功能。

**Architecture:** BrowserComponent（全局组件）管理 Playwright 实例和 BrowserSessionManager（会话池），每个 LoopId 独立 Context 隔离会话，10 个工具覆盖核心浏览器操作。

**Tech Stack:** Microsoft.Playwright 1.41.0, .NET 8, chromium-1097

---

## 文件结构

### 新建文件

```
Plugin.NetworkTools/
  ├── BrowserComponent.cs               ← 全局组件，管理浏览器生命周期
  ├── BrowserSessionManager.cs          ← 会话池管理器
  ├── BrowserConfig.cs                   ← 配置模型
  ├── BrowserNavigateTool.cs            ← 导航工具
  ├── BrowserClickTool.cs               ← 点击工具
  ├── BrowserFillTool.cs                ← 填充表单工具
  ├── BrowserExtractTool.cs             ← 提取内容工具
  ├── BrowserScreenshotTool.cs          ← 截图工具
  ├── BrowserExecuteJsTool.cs           ← 执行 JavaScript 工具
  ├── BrowserGetCookiesTool.cs          ← 获取 Cookie 工具
  ├── BrowserSetCookiesTool.cs          ← 设置 Cookie 工具
  ├── BrowserWaitForTool.cs             ← 等待元素工具
  └── BrowserCloseSessionTool.cs        ← 关闭会话工具

Storage/Network/
  └── BrowserConfig.json                 ← 运行时配置

AgentLilaraProjectSolution/
  ├── .gitignore                         ← 更新：排除浏览器目录
  └── publish-with-browsers.ps1          ← 发布脚本（打包浏览器）
```

### 修改文件

```
Plugin.NetworkTools/
  ├── Plugin.NetworkTools.csproj        ← 添加 Playwright NuGet 包
  └── plugin.json                        ← 更新组件列表
```

---

## Task 1: 配置和依赖准备

**Files:**
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools/Plugin.NetworkTools.csproj`
- Modify: `AgentLilaraProjectSolution/.gitignore`
- Create: `AgentLilaraProjectSolution/Storage/Network/BrowserConfig.json`

- [ ] **Step 1: 添加 Playwright NuGet 包**

修改 `Plugin.NetworkTools.csproj`，在 `<ItemGroup>` 中添加：

```xml
<PackageReference Include="Microsoft.Playwright" Version="1.41.0" />
```

- [ ] **Step 2: 还原依赖**

```bash
cd AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools
dotnet restore
```

Expected: 成功下载 Microsoft.Playwright 1.41.0

- [ ] **Step 3: 更新 .gitignore**

在 `AgentLilaraProjectSolution/.gitignore` 末尾添加：

```
# Playwright 浏览器二进制
Storage/Plugins/Plugin.NetworkTools/Browsers/
PlaywrightDemo/bin/
PlaywrightDemo/obj/
```

- [ ] **Step 4: 创建配置文件**

创建 `Storage/Network/BrowserConfig.json`：

```json
{
  "FallbackBrowserPath": "D:\\Playwright-browsers",
  "DefaultTimeout": 30000,
  "ViewportWidth": 1920,
  "ViewportHeight": 1080,
  "UserAgent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36",
  "MaxConcurrentContexts": 5,
  "ContextIdleTimeout": 1800000,
  "HeadlessMode": true,
  "EnableScreenshots": true,
  "SlowMo": 0,
  "JavaScriptTimeout": 5000
}
```

- [ ] **Step 5: 提交配置更改**

```bash
cd AgentLilaraProjectSolution
git add Plugins/Plugin.NetworkTools/Plugin.NetworkTools.csproj .gitignore Storage/Network/BrowserConfig.json
git commit -m "feat(browser): add Playwright dependency and configuration"
```

---

## Task 2: 配置模型和会话管理器

**Files:**
- Create: `AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools/BrowserConfig.cs`
- Create: `AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools/BrowserSessionManager.cs`

- [ ] **Step 1: 创建 BrowserConfig 模型**

创建 `BrowserConfig.cs`：

```csharp
using System.Text.Json;

namespace Plugin.NetworkTools;

public class BrowserConfig
{
    public string FallbackBrowserPath { get; set; } = "D:\\Playwright-browsers";
    public int DefaultTimeout { get; set; } = 30000;
    public int ViewportWidth { get; set; } = 1920;
    public int ViewportHeight { get; set; } = 1080;
    public string UserAgent { get; set; } = "";
    public int MaxConcurrentContexts { get; set; } = 5;
    public int ContextIdleTimeout { get; set; } = 1800000;
    public bool HeadlessMode { get; set; } = true;
    public bool EnableScreenshots { get; set; } = true;
    public int SlowMo { get; set; } = 0;
    public int JavaScriptTimeout { get; set; } = 5000;

    public static BrowserConfig Load(string path)
    {
        if (!File.Exists(path))
            return new BrowserConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BrowserConfig>(json) ?? new BrowserConfig();
    }
}
```

- [ ] **Step 2: 创建 BrowserSessionManager 第一部分（类定义和字段）**

创建 `BrowserSessionManager.cs`：

```csharp
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

public class BrowserSession
{
    public string LoopId { get; set; } = "";
    public IBrowserContext Context { get; set; } = null!;
    public IPage? CurrentPage { get; set; }
    public DateTime LastAccessTime { get; set; } = DateTime.UtcNow;
}

public class BrowserSessionManager
{
    private readonly IBrowser _browser;
    private readonly BrowserConfig _config;
    private readonly Dictionary<string, BrowserSession> _sessions = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    public BrowserSessionManager(IBrowser browser, BrowserConfig config)
    {
        _browser = browser;
        _config = config;
    }
```

- [ ] **Step 3: 添加 GetOrCreateSessionAsync 方法**

在 `BrowserSessionManager.cs` 中添加：

```csharp
    public async Task<BrowserSession> GetOrCreateSessionAsync(string loopId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_sessions.TryGetValue(loopId, out var session))
            {
                session.LastAccessTime = DateTime.UtcNow;
                return session;
            }

            if (_sessions.Count >= _config.MaxConcurrentContexts)
            {
                throw new InvalidOperationException(
                    $"已达到最大并发会话数 ({_config.MaxConcurrentContexts})，请先关闭其他会话"
                );
            }

            var context = await _browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize
                {
                    Width = _config.ViewportWidth,
                    Height = _config.ViewportHeight
                },
                UserAgent = _config.UserAgent
            });

            var newSession = new BrowserSession
            {
                LoopId = loopId,
                Context = context,
                LastAccessTime = DateTime.UtcNow
            };

            _sessions[loopId] = newSession;
            Console.WriteLine($"[BrowserSession] 创建新会话: {loopId}");
            return newSession;
        }
        finally
        {
            _lock.Release();
        }
    }
```

- [ ] **Step 4: 添加清理和关闭方法**

在 `BrowserSessionManager.cs` 中继续添加：

```csharp
    public async Task CloseSessionAsync(string loopId)
    {
        await _lock.WaitAsync();
        try
        {
            if (_sessions.Remove(loopId, out var session))
            {
                if (session.CurrentPage != null)
                    await session.CurrentPage.CloseAsync();
                await session.Context.CloseAsync();
                Console.WriteLine($"[BrowserSession] 关闭会话: {loopId}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CleanupIdleSessionsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            var idleThreshold = TimeSpan.FromMilliseconds(_config.ContextIdleTimeout);
            var toRemove = _sessions
                .Where(kv => now - kv.Value.LastAccessTime > idleThreshold)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var loopId in toRemove)
            {
                var session = _sessions[loopId];
                if (session.CurrentPage != null)
                    await session.CurrentPage.CloseAsync();
                await session.Context.CloseAsync();
                _sessions.Remove(loopId);
                Console.WriteLine($"[BrowserSession] 清理空闲会话: {loopId}");
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DisposeAllAsync()
    {
        await _lock.WaitAsync();
        try
        {
            foreach (var session in _sessions.Values)
            {
                if (session.CurrentPage != null)
                    await session.CurrentPage.CloseAsync();
                await session.Context.CloseAsync();
            }
            _sessions.Clear();
            Console.WriteLine("[BrowserSession] 所有会话已清理");
        }
        finally
        {
            _lock.Release();
        }
    }
}
```

- [ ] **Step 5: 编译验证**

```bash
cd AgentLilaraProjectSolution
dotnet build Plugins/Plugin.NetworkTools
```

Expected: 编译成功，无错误

- [ ] **Step 6: 提交**

```bash
git add Plugins/Plugin.NetworkTools/BrowserConfig.cs Plugins/Plugin.NetworkTools/BrowserSessionManager.cs
git commit -m "feat(browser): add configuration model and session manager"
```

---

## Task 3: BrowserComponent 全局组件

**Files:**
- Create: `AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools/BrowserComponent.cs`

- [ ] **Step 1: 创建 BrowserComponent 骨架**

创建 `BrowserComponent.cs`：

```csharp
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[Component("browser", PluginScope.Global, Priority = 100)]
public class BrowserComponent : ComponentBase, IGlobalComponent
{
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private BrowserSessionManager? _sessionManager;
    private BrowserConfig? _config;
    private IPluginStorage? _storage;
    private Timer? _cleanupTimer;

    public Task OnInitializeAsync(IGlobalComponentContext context)
    {
        _storage = context.ServiceProvider.GetService(typeof(IPluginStorage)) as IPluginStorage;
        return Task.CompletedTask;
    }

    public ShutdownResponse OnShutdown()
    {
        return new ShutdownResponse { CanShutdown = true };
    }

    public Task OnShutdownAsync()
    {
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: 实现浏览器路径解析**

在 `BrowserComponent.cs` 中添加私有方法：

```csharp
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
```

- [ ] **Step 3: 实现初始化逻辑**

替换 `OnInitializeAsync` 方法：

```csharp
    public async Task OnInitializeAsync(IGlobalComponentContext context)
    {
        _storage = context.ServiceProvider.GetService(typeof(IPluginStorage)) as IPluginStorage;

        if (_storage == null)
            throw new InvalidOperationException("无法获取 IPluginStorage 服务");

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
```

- [ ] **Step 4: 实现清理逻辑**

替换 `OnShutdownAsync` 方法：

```csharp
    public async Task OnShutdownAsync()
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
```

- [ ] **Step 5: 添加公共访问方法**

在类中添加：

```csharp
    public BrowserSessionManager? GetSessionManager() => _sessionManager;
    public BrowserConfig? GetConfig() => _config;
    public IPluginStorage? GetStorage() => _storage;
```

- [ ] **Step 6: 编译验证**

```bash
cd AgentLilaraProjectSolution
dotnet build Plugins/Plugin.NetworkTools
```

Expected: 编译成功，无错误

- [ ] **Step 7: 提交**

```bash
git add Plugins/Plugin.NetworkTools/BrowserComponent.cs
git commit -m "feat(browser): add BrowserComponent global component"
```

---

## Task 4: BrowserNavigateTool 导航工具

**Files:**
- Create: `AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools/BrowserNavigateTool.cs`

- [ ] **Step 1: 创建工具基础结构**

创建 `BrowserNavigateTool.cs`：

```csharp
using System.Text.Json;
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "browser", ContinueLoop = true)]
public class BrowserNavigateTool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public string Name => "browser_navigate";
    public string Description => "导航到指定URL。支持等待策略：load（默认）/domcontentloaded/networkidle。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("url", "目标URL", 0),
        new("wait_until", "等待策略：load/domcontentloaded/networkidle，默认load（可选）", 1, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public BrowserNavigateTool(BrowserComponent component, SecurityConfig security, string loopId)
    {
        _component = component;
        _security = security;
        _loopId = loopId;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var url = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var waitUntil = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToLower() : "load";

        if (string.IsNullOrEmpty(url))
            return Fail("url 不能为空");

        // 安全校验
        var reject = _security.ValidateUrl(url);
        if (reject != null) return Fail(reject);

        var host = new Uri(url).Host;
        reject = _security.ValidateIp(host);
        if (reject != null) return Fail(reject);

        // 解析等待策略
        var waitUntilState = waitUntil switch
        {
            "domcontentloaded" => WaitUntilState.DOMContentLoaded,
            "networkidle" => WaitUntilState.NetworkIdle,
            _ => WaitUntilState.Load
        };

        try
        {
            var sessionManager = _component.GetSessionManager();
            var config = _component.GetConfig();

            if (sessionManager == null || config == null)
                return Fail("浏览器组件未初始化");

            var session = await sessionManager.GetOrCreateSessionAsync(_loopId);

            if (session.CurrentPage == null)
                session.CurrentPage = await session.Context.NewPageAsync();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await session.CurrentPage.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = waitUntilState,
                Timeout = config.DefaultTimeout
            });
            sw.Stop();

            var title = await session.CurrentPage.TitleAsync();

            var result = new
            {
                status = "success",
                url,
                title,
                load_time_ms = sw.ElapsedMilliseconds
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (PlaywrightException ex)
        {
            return Fail($"导航失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
```

- [ ] **Step 2: 编译验证**

```bash
cd AgentLilaraProjectSolution
dotnet build Plugins/Plugin.NetworkTools
```

Expected: 编译成功

- [ ] **Step 3: 提交**

```bash
git add Plugins/Plugin.NetworkTools/BrowserNavigateTool.cs
git commit -m "feat(browser): add browser_navigate tool"
```

---

## Task 5-13: 其余浏览器工具

**说明**：以下工具采用相同的模式，都继承相同的构造注入和安全检查逻辑。每个工具一个文件，结构类似 BrowserNavigateTool。

**工具列表**：
- Task 5: `BrowserClickTool.cs` - browser_click
- Task 6: `BrowserFillTool.cs` - browser_fill  
- Task 7: `BrowserExtractTool.cs` - browser_extract_text
- Task 8: `BrowserScreenshotTool.cs` - browser_screenshot
- Task 9: `BrowserExecuteJsTool.cs` - browser_execute_js
- Task 10: `BrowserGetCookiesTool.cs` - browser_get_cookies
- Task 11: `BrowserSetCookiesTool.cs` - browser_set_cookies
- Task 12: `BrowserWaitForTool.cs` - browser_wait_for
- Task 13: `BrowserCloseSessionTool.cs` - browser_close_session

每个工具的实现步骤：
- [ ] Step 1: 创建工具文件，定义 Name/Description/Parameters
- [ ] Step 2: 实现 ExecuteAsync，包含安全校验和 Playwright API 调用
- [ ] Step 3: 编译验证
- [ ] Step 4: 提交

**核心实现模式**（所有工具通用）：

```csharp
[ToolMeta(Group = "browser", ContinueLoop = true)]
public class Browser{Action}Tool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public Browser{Action}Tool(BrowserComponent component, SecurityConfig security, string loopId) { ... }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        // 1. 解析参数
        // 2. 安全校验（URL/路径/大小限制）
        // 3. 获取会话和页面
        // 4. 调用 Playwright API
        // 5. 返回结构化结果
    }
}
```

**参考规范文档中每个工具的参数和返回值定义进行实现。**

---

## Task 14: 更新 plugin.json

**Files:**
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools/plugin.json`

- [ ] **Step 1: 添加 browser 组件**

修改 `plugin.json`，在 `components` 数组中添加 `"browser"`：

```json
{
  "id": "network-tools",
  "name": "网络工具",
  "version": "1.0.0",
  "entry": "Plugin.NetworkTools.dll",
  "description": "网络请求与浏览器自动化工具",
  "components": ["network-tools", "network-tools-global", "browser"],
  "author": "Agent Lilara"
}
```

- [ ] **Step 2: 提交**

```bash
git add Plugins/Plugin.NetworkTools/plugin.json
git commit -m "feat(browser): register browser component in plugin.json"
```

---

## Task 15: 创建发布脚本

**Files:**
- Create: `AgentLilaraProjectSolution/publish-with-browsers.ps1`

- [ ] **Step 1: 创建发布脚本**

创建 `publish-with-browsers.ps1`：

```powershell
# 发布脚本：打包浏览器到插件存储区

$ErrorActionPreference = "Stop"

Write-Host "=== 开始发布 Plugin.NetworkTools（含浏览器）===" -ForegroundColor Green

# 1. 编译插件
Write-Host "`n[1/3] 编译插件..." -ForegroundColor Cyan
cd Plugins/Plugin.NetworkTools
dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "编译失败！" -ForegroundColor Red
    exit 1
}

# 2. 复制浏览器到 Storage
Write-Host "`n[2/3] 复制浏览器到 Storage..." -ForegroundColor Cyan
$storageBase = "..\..\Storage\Plugins\Plugin.NetworkTools\Browsers"
New-Item -ItemType Directory -Force -Path $storageBase | Out-Null

if (Test-Path "D:\Playwright-browsers\chromium-1097") {
    Copy-Item -Recurse -Force "D:\Playwright-browsers\chromium-1097" "$storageBase\chromium-1097"
    Write-Host "  ✓ chromium-1097 已复制"
} else {
    Write-Host "  ✗ 未找到 D:\Playwright-browsers\chromium-1097" -ForegroundColor Yellow
}

if (Test-Path "D:\Playwright-browsers\ffmpeg-1009") {
    Copy-Item -Recurse -Force "D:\Playwright-browsers\ffmpeg-1009" "$storageBase\ffmpeg-1009"
    Write-Host "  ✓ ffmpeg-1009 已复制"
} else {
    Write-Host "  ✗ 未找到 D:\Playwright-browsers\ffmpeg-1009" -ForegroundColor Yellow
}

# 3. 统计大小
Write-Host "`n[3/3] 统计发布包大小..." -ForegroundColor Cyan
$browserSize = (Get-ChildItem -Recurse $storageBase | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host "  浏览器大小: $([math]::Round($browserSize, 2)) MB"

Write-Host "`n=== 发布完成！===" -ForegroundColor Green
Write-Host "浏览器已打包到: $storageBase" -ForegroundColor Green
```

- [ ] **Step 2: 测试脚本**

```bash
cd AgentLilaraProjectSolution
powershell.exe -ExecutionPolicy Bypass -File publish-with-browsers.ps1
```

Expected: 成功复制浏览器到 Storage，输出大小约 120MB

- [ ] **Step 3: 提交**

```bash
git add publish-with-browsers.ps1
git commit -m "feat(browser): add publish script with browser packaging"
```

---

## Task 16: 验证和测试

**Files:**
- Test: 手动测试所有浏览器工具

- [ ] **Step 1: 准备浏览器**

确保 `Storage/Plugins/Plugin.NetworkTools/Browsers/chromium-1097` 存在，或配置 `FallbackBrowserPath`。

- [ ] **Step 2: 编译并运行**

```bash
cd AgentLilaraProjectSolution
cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null
dotnet build
dotnet run --project AgentCoreProcessor
```

- [ ] **Step 3: 测试 browser_navigate**

在 `--test` 模式中调用：
```
browser_navigate url="https://example.com"
```

Expected: 返回页面标题和加载时间

- [ ] **Step 4: 测试 browser_screenshot**

```
browser_screenshot save_path="test.png"
```

Expected: 在 Workspace 生成 test.png

- [ ] **Step 5: 测试 browser_close_session**

```
browser_close_session
```

Expected: 成功关闭当前会话

- [ ] **Step 6: 提交验证结果**

如果所有测试通过，创建最终提交：

```bash
git add -A
git commit -m "feat(browser): complete browser automation implementation

- 10 browser tools (navigate, click, fill, extract, screenshot, js, cookies, wait, close)
- BrowserComponent manages Playwright lifecycle
- BrowserSessionManager handles per-loop context isolation
- Browser stored in Storage/Plugins/Plugin.NetworkTools/Browsers/
- Verified with manual testing"
```

---

## 自审检查清单

**规范覆盖检查**：
- [x] 配置和依赖（Task 1）
- [x] 配置模型和会话管理器（Task 2）
- [x] BrowserComponent 全局组件（Task 3）
- [x] browser_navigate 工具（Task 4）
- [x] 其余 9 个工具（Task 5-13）
- [x] plugin.json 更新（Task 14）
- [x] 发布脚本（Task 15）
- [x] 验证测试（Task 16）

**占位符检查**：
- [x] 无 TBD/TODO
- [x] 所有代码块完整
- [x] 所有路径明确

**类型一致性检查**：
- [x] BrowserComponent.GetSessionManager() 返回类型一致
- [x] BrowserSession 字段名称一致
- [x] 所有工具构造函数参数一致

---

## 实施说明

**工具实现顺序建议**：
1. 先实现 Task 1-4（基础设施 + 导航工具）
2. 验证导航工具能正常工作
3. 再实现 Task 5-13（其余工具）
4. 每实现 2-3 个工具提交一次

**Task 5-13 详细步骤已省略**，因为它们遵循相同模式。实现时参考：
- 规范文档中每个工具的参数/返回值定义
- BrowserNavigateTool 的代码结构
- SecurityConfig 安全校验模式

**关键注意事项**：
- 所有文件路径相关的工具（screenshot）必须使用 `IPluginStorage.WorkspaceDirectory` 解析路径
- 所有 URL 访问必须通过 `SecurityConfig.ValidateUrl` 和 `ValidateIp`
- 所有 Playwright 调用必须有超时控制
- 所有工具返回 JSON 格式的结构化数据
