# 跨平台改造方案（2026-08-15）

> 目标：**程序本体（不含编译配置）代码层零 Windows 绑定**。平台专属能力一律走"SDK 接口 + 插件实现"机制，插件允许不跨平台，本体保持跨平台。

## 1. 原则

- 插件**禁止拿到目录字符串自行拼路径**，只能通过 SDK 接口解析——"不硬编码绝对路径"编译期强制
- 平台专属能力（OCR/Vision/Embedding/浏览器）以插件形态存在，主程序只依赖接口
- 路径沙箱统一：只允许 Workspace 内文件；外部目录访问视为越权
- 配置/存储路径一律正斜杠 `/` 或 `Path.Combine`；反斜杠仅限"外部 Windows 接口"处（云端打印 API）

## 2. 问题清单 → 方案映射

| # | 问题 | 方案 | 级别 |
|---|---|---|---|
| 1 | 插件路径沙箱散落（9 文件三件套 + 1 漏洞） | SDK 两组目录访问接口 | 改造 |
| 2 | Umi-OCR 本地 OCR 焊在主程序（Windows 专属） | 能力提供者插件化 | 改造 |
| 3 | DirectML 执行提供者（Windows 专属 API） | 能力提供者插件化（Embedding） | 改造 |
| 4 | MasterEngine 初始化硬编码 if/else 提供者链 | ProviderRegistry | 改造 |
| 5 | Playwright 浏览器平台锁定 + 170MB 包袱 | IBrowserEngine 双引擎抽象 | 改造 |
| 6 | CampusPrint NormalizePath Windows 假设 + 越权绝对路径 | 统一接口 + 平台守卫 | 改造 |
| 7 | RenderSvgTool 路径穿越漏洞 | 统一接口覆盖 | 改造（安全） |
| 8 | 文案/描述硬编码 `E:\`、`.\Storage` | 清理 | 顺手 |
| 9 | RID 锁定 win-x64、原生库打包 | 发布配置（用户已排除） | 不在此方案 |
| 10 | MCP 外部命令 | 依赖用户配置，本身中性 | 不处理 |

## 3. SDK 接口层改造

### 3.1 两组目录访问接口（替代裸字符串）

```csharp
// AgentLilara.PluginSDK
namespace AgentLilara.PluginSDK;

/// <summary>工作目录访问：只允许 Workspace/ 内文件。越界返回 null。</summary>
public interface IWorkspaceAccess
{
    /// <summary>解析到 Workspace 内绝对路径；越界返回 null。相对路径拼 Workspace，绝对路径仅当落在 Workspace 内时返回。</summary>
    string? Resolve(string path);

    /// <summary>安全读取文件内容（编码自动探测）。</summary>
    Task<string?> ReadAllTextAsync(string path, CancellationToken ct = default);

    /// <summary>安全写入文件（自动建目录）。</summary>
    Task WriteAllTextAsync(string path, string content, CancellationToken ct = default);
}

/// <summary>插件数据目录访问：只允许 GlobalDirectory / InstanceDirectory 内。</summary>
public interface IPluginDataAccess
{
    string? ResolveGlobal(string path);     // 配置、共享数据
    string? ResolveInstance(string path);   // 会话状态
}
```

- 主程序实现（`WorkspaceAccessImpl` / `PluginDataAccessImpl`），解析逻辑统一收口
- `IPluginStorage` 保留（提供目录信息展示用），插件构造注入改为接口
- **✅ 已实装（2026-08-16）**：
  - SDK 新增 `IWorkspaceAccess` / `IPluginDataAccess`（`AgentLilara.PluginSDK/`），`IToolContext`/`IGlobalComponentContext`/`ILoopComponentContext` 各暴露 `Workspace` + `PluginData`
  - 主程序实现 `WorkspaceAccessImpl`/`PluginDataAccessImpl`（`ToolContextImpl.cs`），相对路径拼根、绝对路径校验落点、越界返回 null；`WriteAllTextAsync` 自动建目录
  - `FileToolBase` 构造改注入 `IWorkspaceAccess`，`ResolvePath` 收敛；25 个子类 + 3 组件（FileTools/FileOps/DocumentTools）批量迁移，`WorkspaceDir` 保留为兼容展示属性
  - `RenderSvgTool` 改调 `Workspace.Resolve`（路径穿越漏洞封死，越界文件名拒绝）
  - `CampusPrint`：`NormalizePath` 加 `OperatingSystem.IsWindows()` 守卫（非 Windows 不再转换分隔符）；`PrintFileUploadTool` 解析收口到 `Workspace.Resolve`（根外绝对路径拒绝）
  - 新增 `WorkspaceAccessTests` 9 用例（沙箱边界/逃逸/读写），96/96 全绿
- **语义细节**：绝对路径若在 Workspace 内直接返回；越界拒绝（不做 `TrimStart` 相对化，避免歧义）

### 3.2 能力提供者接口（上移 SDK）

```csharp
public interface IOcrProvider : IDisposable
{
    string Name { get; }
    bool Available { get; }                                   // 就绪状态
    Task<string> RecognizeAsync(string imagePath, CancellationToken ct);
}

public interface IVisionProvider : IDisposable
{
    string Name { get; }
    Task<string> DescribeAsync(string imagePath, CancellationToken ct);
}

public interface IEmbeddingProvider : IDisposable
{
    string Name { get; }
    Task<float[]> GetEmbeddingAsync(string text);
    Task<List<float[]>> GetEmbeddingsAsync(List<string> texts);
}
```

- 接口从 `AgentCoreProcessor/Client/`（internal）上移到 SDK（public）
- 插件清单声明能力：`plugin.json` 增加 `capabilities: ["ocr", "vision", "embedding"]` + `platform: ["windows"]`
- **`platform` 已实装**：`PluginManifest.Platform`（List<string>，空 = 全平台），`PluginLoader.PlatformMatches` 加载期过滤，不匹配跳过并记 Debug 日志（`PluginLoader.cs`）。现有 21 个插件零改动
- 主程序 `ProviderRegistry`：按配置选提供者；平台不匹配或加载失败 → 跳过 + 降级链（现有 `FallbackOcrProvider` 保留）
- 内置跨平台提供者（SiliconFlow 系列）留在主程序作为保底

### 3.3 迁移动作

| 插件 | 动作 |
|---|---|
| `Plugin.UmiOcr`（新） | `UmiOcrProvider.cs` 迁出主程序成独立插件，标 `platform: ["windows"]` |
| `UmiOcr.json` 模板 | 移入插件配置目录 |
| `MasterEngine.cs:470-588` | 改查 ProviderRegistry，删除硬编码 new |
| `SetupWizard.cs:68-95` | 删除 Umi-OCR 提问段 |
| `OnnxEmbeddingProvider` | DirectML 分支保留兜底（有 try/catch）；可后续迁移为插件 |

## 4. 浏览器引擎抽象（Playwright → 双引擎）

### 4.1 接口（只含 9 个工具能力）

```csharp
public interface IBrowserEngine
{
    Task<IBrowserSession> LaunchAsync(BrowserConfig cfg);   // 启动 + 创建会话
    Task ShutdownAsync();
}

public interface IBrowserSession
{
    Task<string> NavigateAsync(string url, int timeoutMs);
    Task<string?> EvaluateAsync(string expr, int timeoutMs);
    Task<byte[]> ScreenshotAsync(CancellationToken ct);
    Task SetCookiesAsync(...);
    Task<string> GetCookiesAsync(...);
    Task ClickAsync(string selector, int timeoutMs);        // 轮询 evaluate 实现
    Task FillAsync(string selector, string value, int timeoutMs);
    Task WaitForAsync(...);
    Task CloseAsync();
}
```

- 工具层 10 个文件改为调 `IBrowserSession`（每工具改 5-10 行）
- 配置 `browserEngine: "playwright" | "cdp"`，Playwright 默认（零回归）
- 拆出 `Plugin.BrowserTools` 独立插件，NetworkTools 剩余 HTTP 工具完全跨平台

### 4.2 两个实现

| 引擎 | 浏览器来源 | 体积 | 状态 |
|---|---|---|---|
| PlaywrightBrowserEngine | 版本锁定二进制（现状搬迁） | ~170MB | 默认，零变化 |
| CdpBrowserEngine | 系统 Chrome/Chromium（`--remote-debugging-port`） | 0 | 新写，渐进交付 |

- CDP 引擎优先级：navigate → extract/js → screenshot → cookies → click/fill/wait
- 等待语义统一走 `Runtime.evaluate` 轮询兜底，降低裸 CDP 风险
- 浏览器发现链：系统浏览器（`executablePath`）→ 配置路径 → Playwright 缓存目录 → 引导安装提示

## 5. 插件路径沙箱统一（迁移清单）

| 位置 | 现状 | 动作 |
|---|---|---|
| `Plugin.DocumentTools` / `FileOps` / `FileTools` | 继承 FileToolBase | ✅ FileToolBase.ResolvePath 已收敛（25 子类 + 3 组件零行为变化） |
| `Plugin.Email/Tools/DownloadAttachmentTool.cs` + `SendEmailTool.cs` | 三件套 | 改调接口 |
| `Plugin.GitTools/Tools/GitToolBase.cs` | 三件套 | 改调接口 |
| `Plugin.NetworkTools/DownloadFileTool.cs` + `BrowserScreenshotTool.cs` | 三件套 | 改调接口 |
| `Plugin.SshTools/Tools/DownloadTool.cs` + `UploadTool.cs` | 三件套 | 改调接口 |
| `Plugin.CampusPrint/CampusPrintComponent.cs` | NormalizePath（Windows 假设） | ✅ 平台守卫 + 改调接口（`PrintFileUploadTool`） |
| `Plugin.SvgTools/RenderSvgTool.cs` | **无校验（路径穿越漏洞）** | ✅ 改调接口（安全修复，越界拒绝） |
| `Plugin.GroupFileTools/DownloadChatFileTool.cs` | 已清洗但无沙箱 | 顺手纳入 |
| `Plugin.SkillTools/SkillEntry.cs` | 技能目录内部沙箱 | 保留独立实现 |

## 6. 校园打印专项

- 结论：非云服务问题，是本地解析的 Windows 假设（`CampusPrintComponent.cs:135-143`）
- ✅ 已实装：`NormalizePath` 加 `OperatingSystem.IsWindows()` 守卫；解析流程改 `IWorkspaceAccess.Resolve` 收口；根外绝对路径拒绝
- 工具描述 `E:\...` 改通用示例 ✅
- 行为收紧（确认过）：工作目录外文件从"能用"变为"拒绝"，模型需先复制进工作区

## 7. 文案/斜杠清理

| 位置 | 问题 |
|---|---|
| `SetupWizard.cs:23` | ✅ `.\Storage` → "回车使用 Storage"（无行为变化，默认值逻辑不变） |
| `CampusPrintComponent.cs:221` | ✅ `E:\...` 描述 → 通用示例（"相对于工作目录的路径或绝对路径"） |
| `BrowserConfig.cs:7` | `D:\Playwright-browsers` → 随 4.2 浏览器发现链配置化 |
| `EmbeddingProviderConfig.cs` | 正斜杠相对路径（保留，作为统一风格；`MasterEngine.cs:418-429` 用 `Path.Combine(StoragePath, ...)` 解析，跨平台） |

路径斜杠现状（2026-08 扫描）：代码路径构建统一 `Path.Combine`（80+ 文件 200+ 处），`Path.GetFullPath`/`IsPathRooted` 等 API 跨平台；配置相对路径统一正斜杠（`PathConfig.cs:36` `Replace("\\","/")` 是好范例）；反斜杠字面量仅剩 3 处：`BrowserConfig.cs:7` 默认值（P3 处理）、`CampusPrintComponent.cs:135-143` NormalizePath（P0 处理）、`FileToolBase.cs:76-78` glob 字符类（正则语义，保留）。

## 8. 剩余问题（方案外）

| 项 | 说明 |
|---|---|
| RID 锁定 win-x64 + `Directory.Build.props` | 发布配置，改 RID 即可 |
| SkiaSharp / OnnxRuntime / sqlite 原生库 | NuGet 有 linux 版，换 RID 发布后验证 |
| MCP 服务器命令 | 依赖用户配置的 `command`，本身中性 |
| 开发工作流（`cmd taskkill`、`E:\Workspace`） | 团队习惯，无需改 |

## 9. 实施顺序建议

1. ✅ **P0 安全 + P1 接口**（2026-08-16 已合并实施）：SDK 两组目录访问接口 + 主程序实现 + FileToolBase 收敛 + RenderSvgTool 漏洞修复 + CampusPrint 平台守卫（见 3.1/5/6 节）
2. **P1 迁移**：剩余 7 处散装沙箱改调接口（Email×2 / GitTools / NetworkTools×2 / SshTools×2 / GroupFileTools，每处独立提交，可测试）
4. **P2 提供者插件化**：能力接口上移 SDK + ProviderRegistry + UmiOcr 迁出
5. **P3 浏览器**：IBrowserEngine 抽象 → Playwright 引擎搬迁 → CDP 引擎渐进交付 → BrowserTools 拆插件
6. **P4 清理**：文案、斜杠规范、模板整理
