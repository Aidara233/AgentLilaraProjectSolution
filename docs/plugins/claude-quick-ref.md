# 插件系统 — Claude 速查

> 新 Claude 会话进入后，读这份文档就能直接开始改插件，不用 explore。

## 快速定位

| 我要… | 去看 |
|-------|------|
| 新建一个插件 | [quickstart.md](quickstart.md) — 完整代码示例 |
| 查接口/枚举/服务签名 | [api-reference.md](api-reference.md) — 完整 API 参考 |
| 理解加载流程/工具解析 | [README.md](README.md) — 运行时机制 |
| **搞清路径配置** | **[path-config.md](path-config.md)** — Storage vs BaseDirectory、插件数据路径、常见坑 |
| **学习实现模式** | **[patterns.md](patterns.md)** — Static Accessor Bridge、异步通知 Drain、Global Timer、文件沙箱 |
| 看现有插件有哪些工具 | 下方"插件清单" |

## 插件清单（速查版）

### Loop 组件（每循环一个实例）

| 插件 | 适用引擎 | 工具 | 关键文件 |
|------|----------|------|----------|
| **Plugin.BasicTools** | channel | `speak`, `send_media`, `send_file`, `adapter_action` | `BasicToolsComponent.cs` |
| **Plugin.WorkingTools** | channel | `pinboard`, `thinking_notes`, `retain_list`, `task_list`, `mark_for_review` | `WorkingComponent.cs` |
| **Plugin.CrossLoopTools** | channel + system | `send_request`, `evaluate_request`, `complete_request`, `check_messages`, `respond_to_request`, `list_requests`, `list_loops` 等 | `CrossLoopComponent.cs` |
| **Plugin.ScheduledTasks** | channel | `schedule_task`, `cancel_task`, `list_tasks` | `ScheduledTasksComponent.cs` |

### Global 组件（全局单例）

| 插件 | 适用引擎 | 工具 | 关键文件 |
|------|----------|------|----------|
| **Plugin.MemoryTools** | 所有 | `memory_store`, `memory_get`, `memory_update`, `memory_delete`, `memory_search`, `memory_list`, `memory_link_create/delete/get`, `memory_stats` | `MemoryComponent.cs` |
| **Plugin.FileTools** | 所有 | `read_text`, `write_text`, `list_dir`, `move`, `delete`, `copy` | `FileToolsComponent.cs` |
| **Plugin.FileOps** | 所有 | `archive_create`, `archive_extract`, `archive_list`, `search_files`, `grep_files`, `file_info`, `file_hash`, `compare_files` | `FileOpsComponent.cs` |
| **Plugin.ReviewTools** | review only | `review_browse`, `review_search`, `review_evaluate`, `review_save_progress` 等 15 个 | `ReviewComponent.cs` |

### 独立工具（无组件，直接注册到 ToolRegistry）

| 插件 | 工具 |
|------|------|
| **Plugin.SystemTools** | `create_sub_agent`, `stop_sub_agent` |
| **Plugin.SkillTools** | `casual-chat`, `code-review`, `system-maintenance` |
| **Plugin.NetworkTools** | 网络工具 |
| **Plugin.SshTools** | SSH 工具 |
| **Plugin.WebSearch** | 网页搜索 |
| **Plugin.DicePool** | 骰子 |
| **Plugin.ExternalDice** | 外部骰子 |
| **Plugin.ImageTools** | 图片处理 |
| **Plugin.DocumentTools** | 文档处理 |
| **Plugin.GroupFileTools** | 分组文件操作 |
| **Plugin.QuickActions** | 快捷操作 |

## 开发套路

### 1. 新建插件项目

```
Plugins/Plugin.MyNew/Plugin.MyNew.csproj
```

csproj 必须：
```xml
<ProjectReference Include="..\..\AgentLilara.PluginSDK\AgentLilara.PluginSDK.csproj">
  <Private>false</Private>
</ProjectReference>
```

推荐加编译后复制：
```xml
<Target Name="CopyToHostPlugins" AfterTargets="Build">
  <Copy SourceFiles="$(OutputPath)Plugin.MyNew.dll"
        DestinationFolder="$(SolutionDir)AgentCoreProcessor\bin\$(Configuration)\net8.0\Plugins\" />
</Target>
```

### 2. 选模式

- **Global 组件**：`[Component(Scope = ComponentScope.Global)]` + 继承 `GlobalComponentBase`
- **Loop 组件**：`[Component(Scope = ComponentScope.Loop)]` + 继承 `LoopComponentBase`
- **独立工具**：直接实现 `ITool`，构造函数接收 `IToolContext`

### 3. 工具元数据

```csharp
[ToolMeta(
    Group = "my-group",              // WebUI 分组
    ContinueLoop = true,             // 执行后触发下一轮
    ExpressAvailable = true,         // Express 模式可用
    Permission = ToolPermission.Default
)]
```

### 4. 获取宿主服务

组件内工具构造函数接收 `IPluginStorage`，通过 `context.GetService<T>()` 拿服务：

```csharp
// 组件 OnInitAsync 中
var memory = context.GetService<IMemoryAccess>();
var channel = context.GetService<IChannelAccess>();
var messaging = context.GetService<IAgentMessaging>();
var log = context.GetService<ILogAccess>();
```

常用服务：`IMemoryAccess`, `IChannelAccess`, `IAgentMessaging`, `IEngineAccess`, `ILoopControl`, `ILogAccess`, `ISleepAccess`, `IAdapterAccess`, `IPersonAccess`, `IReviewAccess`, `IBeaconAccess`, `IDiceRegistry`

### 5. 编译 + 验证

```bash
dotnet build
# 启动后 WebUI → /p/plugins 确认组件出现
# 或点"热重载"按钮
```

## 常见坑

| 问题 | 原因 | 解决 |
|------|------|------|
| 插件没被加载 | DLL 没复制到 `Plugins/` 目录 | csproj 加 Copy Target，或手动复制 |
| 工具注册失败（名称冲突） | 同名工具已被其他插件注册 | 改工具名，或检查是否重复加载 |
| 组件工具不生效 | DLL 有 `[Component]` 时独立 ITool 不被注册 | 把工具放进组件的 `Tools` 属性 |
| 构造注入拿不到服务 | 服务类型不在 `InstantiateWithInjection` 的 resolver 字典里 | 用 `IServiceProvider` 参数 + `GetService<T>()` |
| Loop 组件在某个引擎不出现 | `[LoopApplicability]` 设了 `Disabled`/`NotApplicable` | 检查属性配置 |
| `OnEnabledAsync()` 初始化时没调用 | 设计如此，首次初始化只调 `OnInitAsync()` | 初始化逻辑放 `OnInitAsync()` |
| 文件路径不对 | 用 `..` 跳出 `GlobalDirectory` 访问 Workspace | 用 `IPluginStorage.WorkspaceDirectory` |
| 以为插件 DLL 在 Storage 下 | 插件 DLL 在 `{BaseDirectory}/Plugins/`，不在 Storage 下 | 读 [path-config.md](path-config.md) |
| 在插件里用 `PathConfig` | 插件不引用 AgentCoreProcessor，拿不到 PathConfig | 用 `IPluginStorage` 接口 |
| 以为 paths.json 在 Storage 下 | paths.json 在 exe 旁边（BaseDirectory） | 读 [path-config.md](path-config.md) |
| 参数名中文导致 API 报错 | Bedrock 代理要求参数名英文 | 参数名全英文 |

## 关键文件路径

```
# 插件 SDK（所有接口定义）
AgentLilara.PluginSDK/ITool.cs
AgentLilara.PluginSDK/ComponentBase.cs          ← GlobalComponentBase / LoopComponentBase
AgentLilara.PluginSDK/ComponentAttribute.cs     ← [Component] / [LoopApplicability]
AgentLilara.PluginSDK/ToolMetaAttribute.cs      ← [ToolMeta] / [PluginDependency]
AgentLilara.PluginSDK/Services/                 ← 18 个服务接口

# 宿主侧（加载 + 生命周期）
AgentCoreProcessor/Tool/Host/PluginLoader.cs    ← 扫描 / 加载 / 热重载
AgentCoreProcessor/Component/ComponentRegistry.cs ← 组件类型注册表
AgentCoreProcessor/Component/ComponentHost.cs   ← Loop 组件宿主
AgentCoreProcessor/Component/GlobalComponentHost.cs ← 全局组件宿主

# 配置
Storage/Engine/ComponentConfig.json             ← 组件启用/禁用状态
Storage/Engine/ToolProfiles.json                ← 工具配置
```
