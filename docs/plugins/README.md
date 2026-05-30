# 插件系统

Agent Lilara 的插件系统允许通过独立的 DLL 扩展功能，无需修改核心代码。所有插件编译为 `.dll`，放在 `Plugins/` 目录下，启动时自动发现和加载。

## 核心概念

插件的两层抽象：

- **组件 (Component)** — 插件的顶层组织单元，管理生命周期、暴露工具列表、注入 prompt 片段。分为 `Global`（全局单例）和 `Loop`（每个引擎循环一个实例）两种作用域。
- **工具 (Tool)** — 实际可被 AI 调用的功能单元。定义名称、描述、参数，通过 `ExecuteAsync` 执行。

组件是容器，工具是内容。一个 DLL 可以只有一个组件 + 多个工具，也可以只有独立工具（无组件）。

## 运行时机制

### 加载流程

```
Program.cs 启动
  └─ PluginLoader.LoadAll()
       └─ 扫描 Plugins/*.dll（递归子目录）
            └─ 每个 DLL 创建 PluginLoadContext（AssemblyLoadContext, isCollectible=true）
                 └─ 5 条发现通道：
                      1. DiscoverToolTypes()      → ITool 实现类
                      2. DiscoverComponentTypes()  → [Component] 标记类
                      3. DiscoverProviderTypes()   → IWebUIProvider 实现类
                      4. DiscoverInjectProviderTypes() → IInjectProvider 实现类
                      5. DiscoverLifecycleTypes()  → IEngineLifecycle 实现类
```

**关键规则**：如果 DLL 中有 `[Component]` 标记的类，工具由组件管理，PluginLoader **不**独立注册该 DLL 中的 ITool。

### 组件实例化

| 阶段 | 谁持有 | 何时创建 |
|------|--------|----------|
| **GlobalComponentHost** | MasterEngine | 启动时 `InitAsync()`，扫描 `ComponentRegistry.GetGlobals()` |
| **ComponentHost** | 每个 ChannelEngine/SystemEngine/ReviewEngine | 引擎初始化时 `InitAsync()`，扫描 `ComponentRegistry.GetLoopComponents(loopType)` |

构造函数注入优先级（`PluginLoader.InstantiateWithInjection`）：
1. 含 `IToolContext` 参数的构造函数（独立工具）
2. 含 `IPluginStorage` 参数的构造函数（组件内工具）
3. 含 `IServiceProvider` 参数的构造函数（组件本身）
4. 无参构造函数

可注入的服务：`EventBus`, `ModuleBus`, `Gate`, `IMemoryAccess`, 以及 `IServiceProvider` 能解析的任何类型。

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
    Plugin.BasicTools/            ← 基础通信（speak, send_media, send_file, adapter_action）— Loop, channel only
    Plugin.WorkingTools/          ← 工作空间（pinboard, thinking_notes, retain_list, task_list, mark_for_review）— Loop, channel only
    Plugin.MemoryTools/           ← 记忆存储/检索/关联（10个工具）— Global
    Plugin.FileTools/             ← 文件操作（read_text, write_text, list_dir, move, delete, copy）— Global
    Plugin.FileOps/               ← 高级文件操作（archive_create/extract/list, search_files, grep, file_info, file_hash, compare）— Global
    Plugin.SystemTools/           ← 子 agent 管理（create_sub_agent, stop_sub_agent）
    Plugin.ReviewTools/           ← 复盘工具（15个 review_* 工具）— Global, review only
    Plugin.CrossLoopTools/        ← 跨循环通信（12个工具）— Loop, channel + system
    Plugin.ScheduledTasks/        ← 定时任务（schedule/cancel/list）— Loop, channel only
    Plugin.Email/                 ← 邮件收发（send_email, check_unread, check_email, read_email, search_email, download_attachment, delete_email, mark_all_read, list_folders）— Global
    Plugin.SkillTools/            ← 技能工具（casual-chat, code-review, system-maintenance）
    Plugin.NetworkTools/          ← 网络工具
    Plugin.SshTools/              ← SSH 工具
    Plugin.WebSearch/             ← 网页搜索
    Plugin.DicePool/              ← 骰子系统
    Plugin.ExternalDice/          ← 外部骰子
    Plugin.ImageTools/            ← 图片处理
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
| [实现模式](patterns.md) | Static Accessor Bridge、异步通知 Drain、Global Timer、文件沙箱等 |
| [Claude 速查](claude-quick-ref.md) | 给新 Claude 会话的快速上手指南 |

## 关键设计决策

- **组件优先**：如果 DLL 中有 `[Component]` 标记的类，工具由组件管理而非独立注册
- **AssemblyLoadContext 隔离**：每个插件 DLL 运行在自己的 ALC 中，支持热重载（卸载后重新扫描）
- **服务定位器模式**：插件通过 `GetService<T>()` 获取宿主服务（IMemoryAccess、IAgentMessaging 等），不直接引用宿主程序集
- **无清单文件**：所有元数据通过 C# 属性声明（`[Component]`、`[ToolMeta]`、`[LoopApplicability]` 等）
- **参数名必须英文**：这是 Bedrock 代理的硬性限制
- **Workspace 路径**：文件工具通过 `IPluginStorage.WorkspaceDirectory` 访问共享沙箱，不要用 `..` 跳转
- **共享代码**：跨插件复用抽到独立类库（如 `FileToolKit.Shared/`），不用链接文件
