# 无头浏览器自动化集成设计

**日期**: 2026-06-17  
**状态**: 设计阶段  
**实现方式**: 扩展 Plugin.NetworkTools

## 目标

为 Agent Lilara 添加无头浏览器能力，支持：
- 动态网页抓取（需要 JavaScript 渲染的 SPA 应用）
- 网页自动化操作（点击、填表、滚动等复杂交互）
- 网页截图和内容提取
- Cookie/Session 管理，保持登录态

## 技术选型

**方案**: Microsoft Playwright for .NET

**理由**:
- 官方维护，.NET 生态集成最好
- 支持 Chromium/Firefox/WebKit 三种引擎
- 功能完整：截图、PDF、Cookie、JS 执行、选择器等
- 自动管理浏览器二进制
- 活跃开发，是浏览器自动化行业标准

**验证**: 已通过独立 demo 验证（见 `PlaywrightDemo/`），9 个场景全部通过。

**版本锁定**:
- NuGet 包: `Microsoft.Playwright` 1.41.0
- 浏览器: `chromium-1097` (Chromium 121.0.6167.57)
- 存储位置: `Storage/Plugins/Plugin.NetworkTools/Browsers/`（使用 `IPluginStorage.GlobalDirectory`）

## 浏览器分发策略

### 开发环境

浏览器不纳入 Git，开发者首次运行时需手动安装：
```bash
cd PlaywrightDemo
powershell.exe -ExecutionPolicy Bypass -File install-browser.ps1
```

安装完成后，将 `D:\Playwright-browsers\` 内容复制到：
```
Storage/Plugins/Plugin.NetworkTools/Browsers/
```

### 生产环境（Release）

发布时使用 `publish-with-browsers.ps1` 脚本自动打包浏览器到插件存储区：
```powershell
# 编译插件
dotnet build Plugins/Plugin.NetworkTools -c Release

# 复制浏览器到 Storage 插件目录
$storageBase = "Storage/Plugins/Plugin.NetworkTools/Browsers"
New-Item -ItemType Directory -Force -Path $storageBase
Copy-Item -Recurse "D:\Playwright-browsers\chromium-1097" "$storageBase\chromium-1097"
Copy-Item -Recurse "D:\Playwright-browsers\ffmpeg-1009" "$storageBase\ffmpeg-1009"

Write-Host "浏览器已打包到 Storage（~120MB）"
```

### 路径解析逻辑

`BrowserComponent` 初始化时按优先级查找浏览器：

```csharp
private string ResolveBrowserPath()
{
    // 1. 优先使用插件存储区（生产环境）
    var storageDir = Path.Combine(_pluginStorage.GlobalDirectory, "Browsers");
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

### .gitignore 配置

```
# 排除浏览器二进制（开发环境）
Storage/Plugins/Plugin.NetworkTools/Browsers/
PlaywrightDemo/bin/
PlaywrightDemo/obj/
D:/Playwright-browsers/
```

### 优势

- **存储区隔离**：浏览器在 `Storage/` 下，与代码分离，不污染插件目录
- **易于管理**：用户可以直接删除 `Storage/Plugins/Plugin.NetworkTools/` 清理浏览器
- **发布友好**：Release 包含浏览器，开箱即用
- **开发轻量**：Git 不包含 120MB 浏览器文件
- **路径稳定**：`IPluginStorage.GlobalDirectory` 保证路径一致性

## 架构设计

### 组件结构

```
Plugin.NetworkTools/
  ├── BrowserComponent.cs           ← 全局组件，管理浏览器生命周期
  ├── BrowserSessionManager.cs      ← 会话池管理器
  ├── BrowserConfig.cs               ← 配置模型
  ├── Tools/
  │   ├── BrowserNavigateTool.cs    ← 导航到 URL
  │   ├── BrowserClickTool.cs       ← 点击元素
  │   ├── BrowserFillTool.cs        ← 填充表单
  │   ├── BrowserExtractTool.cs     ← 提取文本/HTML
  │   ├── BrowserScreenshotTool.cs  ← 截图
  │   ├── BrowserExecuteJsTool.cs   ← 执行 JavaScript
  │   ├── BrowserGetCookiesTool.cs  ← 获取 Cookie
  │   ├── BrowserSetCookiesTool.cs  ← 设置 Cookie
  │   ├── BrowserWaitForTool.cs     ← 等待元素/状态
  │   └── BrowserCloseSessionTool.cs ← 关闭会话
  └── plugin.json                    ← 更新组件列表

Storage/Network/
  └── BrowserConfig.json             ← 运行时配置
```

### 核心组件：BrowserComponent

**类型**: IGlobalComponent  
**职责**:
- 初始化时启动 Playwright
- 管理 BrowserSessionManager 实例
- 清理时关闭所有浏览器实例
- 注册所有浏览器工具

**生命周期**:
```
OnInitializeAsync()
  → Playwright.CreateAsync()
  → playwright.Chromium.LaunchAsync()
  → 创建 BrowserSessionManager

OnShutdownAsync()
  → BrowserSessionManager.DisposeAllAsync()
  → browser.CloseAsync()
  → playwright.Dispose()
```

### 会话管理：BrowserSessionManager

**职责**:
- 为每个 LoopId 维护独立的 BrowserContext
- 自动回收空闲会话（超时 30 分钟）
- 隔离不同循环的 Cookie/Storage

**数据结构**:
```csharp
class BrowserSession
{
    public string LoopId { get; set; }
    public IBrowserContext Context { get; set; }
    public IPage? CurrentPage { get; set; }
    public DateTime LastAccessTime { get; set; }
}

Dictionary<string, BrowserSession> _sessions;
```

**API**:
```csharp
Task<BrowserSession> GetOrCreateSessionAsync(string loopId)
Task CloseSessionAsync(string loopId)
Task CleanupIdleSessionsAsync()  // 定时清理
```

### 工具设计

所有工具继承现有安全框架，复用 `SecurityConfig`。

#### 1. browser_navigate

**参数**:
- `url` (必需): 目标 URL
- `wait_until` (可选): 等待策略，默认 `load`
  - `load` - 等待 load 事件
  - `domcontentloaded` - 等待 DOMContentLoaded
  - `networkidle` - 等待网络空闲

**返回**:
```json
{
  "status": "success",
  "url": "https://example.com",
  "title": "Example Domain",
  "load_time_ms": 1234
}
```

**实现要点**:
- URL 安全校验（复用 `SecurityConfig.ValidateUrl`）
- IP 黑名单检查
- 超时控制（从配置读取）

#### 2. browser_click

**参数**:
- `selector` (必需): CSS 选择器或文本选择器
- `button` (可选): 鼠标按键，默认 `left`
- `click_count` (可选): 点击次数，默认 1

**返回**:
```json
{
  "status": "success",
  "selector": "#submit-button",
  "clicked": true
}
```

**实现要点**:
- 自动等待元素可见、启用、可操作
- 支持多种选择器：CSS、文本、ARIA 标签

#### 3. browser_fill

**参数**:
- `selector` (必需): 表单字段选择器
- `value` (必需): 填充值

**返回**:
```json
{
  "status": "success",
  "selector": "#username",
  "filled": true
}
```

#### 4. browser_extract_text

**参数**:
- `selector` (可选): CSS 选择器，省略则提取整个 body
- `extract_type` (可选): 提取类型，默认 `text`
  - `text` - 纯文本
  - `html` - HTML 源码
  - `inner_html` - 内部 HTML
- `multiple` (可选): 匹配多个元素时是否全部提取，默认 `false`（仅提取第一个）

**返回**:
```json
{
  "status": "success",
  "selector": "h1",
  "content": "Example Domain",
  "extract_type": "text",
  "match_count": 1
}
```

当 `multiple: true` 时：
```json
{
  "status": "success",
  "selector": "p",
  "contents": ["First paragraph", "Second paragraph"],
  "extract_type": "text",
  "match_count": 2
}
```

**实现要点**:
- 默认提取第一个匹配元素（避免意外返回大量数据）
- `multiple: true` 时返回数组，最多 100 个元素
- 大文本截断（继承现有 `MaxResponseBodyBytes` 限制）

#### 5. browser_screenshot

**参数**:
- `save_path` (必需): 相对于 Workspace 的保存路径
- `full_page` (可选): 是否截取完整页面，默认 `false`
- `selector` (可选): 截取特定元素

**返回**:
```json
{
  "status": "success",
  "file_path": "screenshots/page.png",
  "size_bytes": 98304
}
```

**实现要点**:
- 路径沙箱校验（必须在 Workspace 内）
- 支持 PNG/JPEG 格式
- 全页截图自动滚动

#### 6. browser_execute_js

**参数**:
- `script` (必需): JavaScript 代码
- `args` (可选): 传递给脚本的参数（JSON 数组）

**返回**:
```json
{
  "status": "success",
  "result": "Example Domain"
}
```

**安全限制**:
- 只读操作为主（避免破坏页面状态）
- 超时控制（默认 5 秒）
- 结果大小限制

#### 7. browser_get_cookies

**参数**:
- `url` (可选): 获取特定 URL 的 Cookie，省略则获取所有

**返回**:
```json
{
  "status": "success",
  "cookies": [
    {
      "name": "session_id",
      "value": "abc123",
      "domain": ".example.com",
      "path": "/",
      "expires": 1735689600,
      "httpOnly": true,
      "secure": true
    }
  ]
}
```

#### 8. browser_set_cookies

**参数**:
- `cookies` (必需): Cookie 数组（JSON）

**返回**:
```json
{
  "status": "success",
  "set_count": 2
}
```

#### 9. browser_wait_for

**参数**:
- `selector` (必需): 等待的选择器
- `state` (可选): 等待状态，默认 `visible`
  - `visible` - 可见
  - `hidden` - 隐藏
  - `attached` - 存在于 DOM
  - `detached` - 从 DOM 移除

**返回**:
```json
{
  "status": "success",
  "selector": ".loading-spinner",
  "state": "hidden",
  "wait_time_ms": 823
}
```

#### 10. browser_close_session

**参数**: 无（自动从工具上下文获取 LoopId）

**返回**:
```json
{
  "status": "success",
  "session_closed": true
}
```

**实现要点**:
- 关闭当前 LoopId 的 BrowserContext
- 释放所有相关资源
- 下次调用自动创建新会话

## 配置设计

### BrowserConfig.json

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
  "EnablePdf": false,
  "SlowMo": 0,
  "JavaScriptTimeout": 5000
}
```

**字段说明**:
- `FallbackBrowserPath`: 降级浏览器路径（开发环境，生产优先用插件存储区）
- `DefaultTimeout`: 默认操作超时（毫秒）
- `ViewportWidth/Height`: 视口大小
- `UserAgent`: 自定义 UA
- `MaxConcurrentContexts`: 最大并发会话数
- `ContextIdleTimeout`: 会话空闲超时（毫秒）
- `HeadlessMode`: 是否无头模式
- `SlowMo`: 调试用，每个操作延迟（毫秒）
- `JavaScriptTimeout`: JS 执行超时（毫秒）

## 安全设计

### URL 安全

继承现有 `SecurityConfig.ValidateUrl`：
- 黑名单域名拦截
- 内网 IP 拦截（127.0.0.1、192.168.x.x、10.x.x.x）
- 协议限制（仅 http/https）

### 文件安全

- 截图保存路径强制沙箱校验（Workspace 内）
- 禁止访问系统路径（`C:\Windows`、`/etc` 等）

### JavaScript 安全

- 执行超时限制（默认 5 秒）
- 结果大小限制（继承 `MaxResponseBodyBytes`）
- 不允许修改 `window.location`（防止意外跳转）

### 资源限制

- 最大并发 Context 数量（防止内存耗尽）
- 空闲会话自动回收
- 下载文件大小限制（与 `DownloadFileTool` 一致）

## 性能优化

### 1. 按需启动

- 首次调用浏览器工具时才启动 Chromium 进程
- 未使用浏览器功能时零开销

### 2. Context 复用

- 每个 LoopId 复用同一个 BrowserContext
- 避免频繁创建/销毁的性能开销
- 保持会话状态（Cookie、LocalStorage）

### 3. 页面池化

- 每个 Context 保留最后一个活动页面
- 导航复用现有 Page，避免创建新标签

### 4. 定时清理

- 后台定时任务（每 5 分钟）清理空闲会话
- 超过 30 分钟无操作自动关闭

### 5. 资源监控

- 记录活跃 Context 数量
- 达到上限时拒绝新建（返回错误，提示先关闭）

## 部署流程

### 首次部署

1. **安装浏览器**（在目标机器执行）：
   ```bash
   cd PlaywrightDemo
   powershell.exe -ExecutionPolicy Bypass -File install-browser.ps1
   ```

2. **验证安装**：
   ```bash
   # 确认目录存在
   dir D:\Playwright-browsers\chromium-1097\chrome-win\chrome.exe
   ```

3. **配置文件**：
   - 复制 `templates/Network/BrowserConfig.json` 到 `Storage/Network/`
   - 确认 `FallbackBrowserPath` 指向正确路径（开发环境降级用）

4. **编译插件**：
   ```bash
   cd AgentLilaraProjectSolution
   dotnet build Plugins/Plugin.NetworkTools
   ```

5. **热重载**：
   - WebUI `/p/plugins` 页面点击"热重载"
   - 或重启 AgentCoreProcessor

### 更新流程

当 Playwright 版本升级时：

1. 修改 `Plugin.NetworkTools.csproj` 中的版本号
2. 编译项目
3. 运行新的 `playwright.ps1 install chromium`
4. 更新 `BrowserConfig.json` 中的路径（如果浏览器版本号变化）
5. 热重载插件

## 故障排查

### 问题 1: 找不到浏览器

**现象**:
```
Executable doesn't exist at D:\Playwright-browsers\chromium-1097\chrome-win\chrome.exe
```

**排查**:
1. 检查 `BrowserConfig.json` 中的 `FallbackBrowserPath`
2. 确认 `Storage/Plugins/Plugin.NetworkTools/Browsers/chromium-1097` 目录存在
3. 重新运行 `install-browser.ps1` 或复制浏览器到插件存储区

### 问题 2: 超时

**现象**:
```
Timeout 30000ms exceeded
```

**排查**:
1. 检查目标网站是否可访问
2. 增加 `DefaultTimeout` 值
3. 使用 `HeadlessMode: false` 调试观察

### 问题 3: 内存占用高

**现象**: 多个 Chromium 进程，内存占用过高

**排查**:
1. 检查活跃 Context 数量（WebUI 后续可加监控卡片）
2. 手动调用 `browser_close_session` 释放不用的会话
3. 降低 `MaxConcurrentContexts`
4. 减少 `ContextIdleTimeout`

### 问题 4: 选择器找不到元素

**现象**:
```
Timeout waiting for selector "#button"
```

**排查**:
1. 使用 `browser_screenshot` 查看页面状态
2. 使用 `browser_execute_js` 检查元素是否存在
3. 尝试不同选择器策略（文本、ARIA、XPath）
4. 添加 `browser_wait_for` 等待页面加载完成

## 测试计划

### 单元测试

- BrowserSessionManager 会话创建/复用/清理
- 配置加载和验证
- 安全校验（URL、路径、IP）

### 集成测试

- 启动浏览器并导航
- 表单填充和提交
- Cookie 读写
- 截图保存
- JavaScript 执行

### 端到端测试

**场景 1: 动态内容抓取**
```
1. 导航到某 SPA 应用
2. 等待内容加载
3. 提取文本
4. 验证提取结果
```

**场景 2: 表单自动化**
```
1. 导航到登录页
2. 填充用户名/密码
3. 点击登录按钮
4. 验证跳转成功
```

**场景 3: 会话隔离**
```
1. 频道 A 设置 Cookie
2. 频道 B 读取 Cookie
3. 验证频道 B 无法读到频道 A 的 Cookie
```

**场景 4: 资源回收**
```
1. 创建 5 个会话
2. 等待 ContextIdleTimeout
3. 验证空闲会话被自动清理
```

## 后续优化方向

### Phase 1（当前设计）
- 基础浏览器操作（导航、点击、填表、截图）
- 会话管理和资源回收

### Phase 2（后续迭代）
- PDF 生成支持
- 网络拦截和修改（mock API）
- 文件上传/下载集成
- 视频录制

### Phase 3（高级功能）
- Firefox/WebKit 引擎支持
- 分布式浏览器集群
- 代理配置（绕过地域限制）
- 自动化脚本录制（codegen）

## 风险与缓解

| 风险 | 影响 | 概率 | 缓解措施 |
|------|------|------|----------|
| 浏览器启动慢 | 用户体验差 | 中 | 按需启动 + 会话复用 |
| 内存占用高 | 系统资源耗尽 | 高 | 并发限制 + 空闲回收 |
| 网站反爬虫 | 工具失效 | 中 | 自定义 UA + 可配置延迟 |
| 版本不匹配 | 浏览器启动失败 | 低 | 版本锁定 + 安装文档 |
| 选择器变化 | 自动化失败 | 高 | Agent 自适应（多选择器尝试）|

## 参考资料

- [Playwright for .NET 官方文档](https://playwright.dev/dotnet/)
- [验证 Demo 项目](../../PlaywrightDemo/)
- [现有网络工具实现](../../Plugins/Plugin.NetworkTools/)
- [插件系统文档](../plugins/README.md)
