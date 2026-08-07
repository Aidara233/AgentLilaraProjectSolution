# 插件系统

Agent Lilara 的插件系统允许通过独立的 DLL 扩展功能，无需修改核心代码。所有插件编译为 `.dll`，放在 `Plugins/` 目录下，启动时自动发现和加载。

## 核心概念

插件的两层抽象：

- **组件 (Component)** — 插件的顶层组织单元，管理生命周期、暴露工具列表、注入 prompt 片段。分为 `Global`（全局单例）和 `Loop`（每个会话一个实例）两种作用域。
- **工具 (Tool)** — 实际可被 AI 调用的功能单元。定义名称、描述、参数，通过 `ExecuteAsync` 执行。

组件是容器，工具是内容。一个 DLL 可以有多个组件（如 NetworkTools 的 `network-tools` Loop 组件 + `network-tools-global` Global 组件）。当前所有插件均已组件化，不再有"无组件独立工具"模式。

## 运行时机制

### 目录规范

每个插件拥有独立子目录，内部包含 `plugin.json` 清单文件声明自身信息：

```
Plugins/                          ← 宿主扫描目录
  Plugin.Email/                   ← 子目录 = 一个插件
    plugin.json                   ← 清单（必须，声明 id/entry/version 等）
    Plugin.Email.dll              ← 入口程序集
    Plugin.Email.deps.json        ← 依赖映射
    MailKit.dll                   ← NuGet 依赖
    MimeKit.dll
    ...
```

`plugin.json` 格式：
```json
{
  "id": "email",
  "name": "邮件工具",
  "version": "1.0.0",
  "entry": "Plugin.Email.dll",
  "components": ["email"],
  "description": "邮件收发工具（SMTP/IMAP，单账户）",
  "author": "Agent Lilara"
}
```

- `id` — 全局唯一标识，用于 WebUI 路由和热重载
- `entry` — 入口 DLL 文件名（相对于插件目录）
- `components` — 该插件注册的组件名列表（WebUI 展示用）
- 缺少 `plugin.json` 的子目录会被静默跳过

### 加载流程

```
Program.cs 启动
  └─ PluginLoader.LoadAll()
       └─ 扫描 Plugins/ 子目录
            └─ 读取 plugin.json
            └─ 创建 PluginLoadContext(entry.dll)（AssemblyLoadContext, isCollectible=true）
                 └─ deps.json + 目录回退双路径解析依赖
                 └─ 5 条发现通道：
                      1. DiscoverToolTypes()      → ITool 实现类
                      2. DiscoverComponentTypes()  → [Component] 标记类
                      3. DiscoverProviderTypes()   → IWebUIProvider 实现类
                      4. DiscoverInjectProviderTypes() → IInjectProvider 实现类
                      5. DiscoverLifecycleTypes()  → IEngineLifecycle 实现类
```

依赖 DLL 不会被独立扫描，消除竞态条件。依赖解析顺序：
1. `AssemblyDependencyResolver`（基于 `.deps.json`）
2. 目录回退：在插件目录下直接探测同名 DLL

**关键规则**：如果 DLL 中有 `[Component]` 标记的类，工具由组件管理，PluginLoader **不**独立注册该 DLL 中的 ITool。

### 组件实例化

| 阶段 | 谁持有 | 何时创建 |
|------|--------|----------|
| **GlobalComponentHost** | MasterEngine | 启动时 `InitAsync()`，扫描 `ComponentRegistry.GetGlobals()` |
| **ComponentHost** | 每个会话（RuntimeSession） | 引擎初始化时 `InitAsync()`，扫描 `ComponentRegistry.GetLoopComponents(loopType)` |

构造函数注入（`PluginLoader.InstantiateWithInjection`）：按**构造参数个数降序**尝试所有构造函数，逐个参数按内置 resolver 字典（`EventBus` / `ModuleBus` / `Gate` / `IMemoryAccess`）→ `IServiceProvider` → `GetService<T>()` 解析；首个参数全部可解析的构造被选中。

可注入的服务：`EventBus`, `ModuleBus`, `Gate`, `IMemoryAccess`, 以及 `IServiceProvider` 能解析的任何类型（含 `IToolContext`、`IPluginStorage`）。

**初始化超时兜底（2026-08-06）**：组件 `OnInitAsync` 一律带超时，任何组件挂起都不会拖住启动或 session 创建——
- Global 组件：`GlobalComponentHost.InitAsync` **并行**初始化，每组件超时 `GlobalInitTimeoutSeconds`（默认 10s）；超时组件跳过工具注册，其余照常。
- Loop 组件：`ComponentHost.InitAsync` 每组件超时 `LoopInitTimeoutSeconds`（默认 5s）；超时跳过不抛异常，session 照常创建（express 会话在 `eventLock` 下创建，阻塞=卡整条消息路由）。
- 超时值收敛为代码常量（`Component/ComponentTimeouts.cs`：`GlobalInitTimeoutSeconds` / `LoopInitTimeoutSeconds` / `ShutdownTimeoutMs`，不再外部可配）。

**重活组件应懒启动**：不要在 `OnInitAsync` 里做可能挂起的同步操作（网络连接、拉起浏览器等）。参照 `browser`（`EnsureStartedAsync()` 首次工具调用才拉 Playwright/Chromium）与 SSH（全局组件懒连接 + 远端目录后台创建）。

### 工具解析链

引擎调用工具时，按以下优先级查找：

```
ComponentHost.TryGetTool(name)
  → 本地 Loop 组件工具 (_localTools)
  → GlobalComponentHost.TryGetTool(name)
  → ToolRegistry.Get(name)          ← 独立工具 / 全局组件工具
```

Loop 组件工具 **遮蔽** 全局同名工具。

### Prompt 注入流程

每轮 AI 调用前：
1. `GlobalComponentHost.BuildPromptSections(caller)` — 按 `PromptPriority` 升序收集全局组件
2. `ComponentHost.BuildPromptSections()` — 按 `PromptPriority` 升序收集 Loop 组件
3. 结果拼接到 system prompt 末尾

### 热重载

`PluginLoader.ReloadAll()` → `UnloadAll()`（注销工具/组件/Provider → ALC.Unload()）→ `LoadAll()` 重新扫描。
WebUI `/p/plugins` 页面有"热重载"按钮。

## 目录结构

```
AgentLilaraProjectSolution/
  AgentLilara.PluginSDK/         ← 共享契约（引用此项目即可开发插件）
  Plugins/                        ← 所有插件项目
    Plugin.BasicTools/            ← 基础通信与状态（speak, thinking, send_media, send_file, adapter_action, list_channels, search_chat_history, list_sessions, send_session_message）— Loop
    Plugin.WorkingTools/          ← 工作空间（pinboard, thinking_notes, retain_list, task_management, mark_for_review）— Loop
    Plugin.MemoryTools/           ← 记忆存储/检索/关联（10个工具）— Global
    Plugin.FileTools/             ← 文件操作（read_text, write_text, update_text, list_dir, move_file, delete_file, copy_file）— Global
    Plugin.FileOps/               ← 高级文件操作（archive_create/extract/list, search_files, grep_files, file_info, file_hash, compare_files）— Global
    Plugin.SystemTools/           ← 子 session 管理（system-ops 组件：create_sub_agent/stop_sub_agent/send_instruction/list_sub_agents/wait_sub/get_sub_info；sub-agent-runtime 组件：report_to_parent）
    Plugin.ReviewTools/           ← 复盘工具（12个 review_* 工具）— Global
    Plugin.ScheduledTasks/        ← 定时任务（schedule_task/cancel_task/list_tasks/dismiss_notification）— Loop
    Plugin.Email/                 ← 邮件收发（send_email, check_unread, check_email, read_email, search_email, download_attachment, delete_email, mark_all_read, list_folders）— Global
    Plugin.SkillTools/            ← 技能工具（casual-chat, code-review, system-maintenance）
    Plugin.NetworkTools/          ← 网络工具（http_request, download_file + Playwright 浏览器自动化 10 个工具）— Global + Loop
    Plugin.SshTools/              ← SSH 工具
    Plugin.WebSearch/             ← 网页搜索
    Plugin.GitTools/              ← Git/GitHub 操作（git_*, github_*）— Loop + Global
    Plugin.Currency/              ← 货币工具 — Global
    Plugin.CampusPrint/           ← 校园打印 — Global
    Plugin.ImageTools/            ← 图片读取与OCR
    Plugin.SvgTools/              ← SVG 渲染（render_svg）— Global
    Plugin.DocumentTools/         ← 文档处理
    Plugin.GroupFileTools/        ← 分组文件操作
    Plugin.QuickActions/          ← 快捷操作
    FileToolKit.Shared/           ← FileToolBase 共享基类（文件插件共用）
  AgentCoreProcessor/             ← 宿主应用
    Tool/Host/PluginLoader.cs     ← 插件加载器
    Component/ComponentRegistry.cs ← 组件类型注册表
    Component/ComponentHost.cs    ← Loop 组件宿主（每引擎一个实例）
    Component/GlobalComponentHost.cs ← 全局组件宿主（MasterEngine 持有）
```

## 文档导航

| 文档 | 内容 |
|------|------|
| [快速上手](quickstart.md) | 从零创建一个插件，含完整代码示例 |
| [API 参考](api-reference.md) | 所有接口、属性、枚举、服务的完整说明 |
| [路径配置](path-config.md) | Storage vs BaseDirectory、插件数据路径、常见坑 |
| [实现模式](patterns.md) | Static Accessor Bridge、异步通知 Drain、Global Timer + Gate 契约、SpawnCheck 并发安全、文件沙箱等 |
| [Claude 速查](claude-quick-ref.md) | 给新 Claude 会话的快速上手指南 |

## 关键设计决策

- **组件优先**：如果 DLL 中有 `[Component]` 标记的类，工具由组件管理而非独立注册
- **AssemblyLoadContext 隔离**：每个插件 DLL 运行在自己的 ALC 中，支持热重载（卸载后重新扫描）
- **服务定位器模式**：插件通过 `GetService<T>()` 获取宿主服务（IMemoryAccess、IChannelAccess 等），不直接引用宿主程序集
- **无清单文件**：所有元数据通过 C# 属性声明（`[Component]`、`[ToolMeta]` 等）
- **插件不声明权限/引擎适用性**：`[ToolMeta] EngineTypes` 与 `[LoopApplicability]` 的引擎维度已 **删除**。工具/组件可见性完全由程序本体裁决——**会话类型文件即唯一权限源**（`Storage/SessionTypes/{type}.json` 的 Tools 白黑名单裁决工具 + Components 清单裁决组件装配）。插件只需提供工具，不决定谁能用。旧 `PermissionService` / `ComponentConfig` 已删除。
- **参数名必须英文**：这是 Bedrock 代理的硬性限制
- **Workspace 路径**：文件工具通过 `IPluginStorage.WorkspaceDirectory` 访问共享沙箱，不要用 `..` 跳转
- **共享代码**：跨插件复用抽到独立类库（如 `FileToolKit.Shared/`），不用链接文件
