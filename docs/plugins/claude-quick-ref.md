# 插件系统 — Claude 速查

> 新 Claude 会话进入后，读这份文档就能直接开始改插件，不用 explore。

## 快速定位

| 我要… | 去看 |
|-------|------|
| 新建一个插件 | [quickstart.md](quickstart.md) — 完整代码示例 |
| 查接口/枚举/服务签名 | [api-reference.md](api-reference.md) — 完整 API 参考 |
| 理解加载流程/工具解析 | [README.md](README.md) — 运行时机制 |
| **搞清路径配置** | **[path-config.md](path-config.md)** — Storage vs BaseDirectory、插件数据路径、常见坑 |
| **学习实现模式** | **[patterns.md](patterns.md)** — Static Accessor Bridge、异步通知 Drain、Global Timer + Gate 契约、SpawnCheck 并发安全、文件沙箱 |
| 看现有插件有哪些工具 | 下方"插件清单" |

## 插件清单（速查版）

> 所有插件均已组件化（无"无组件独立工具"模式）。作用域分 Global（全局单例，19 个组件）和 Loop（每个会话一个实例，13 个组件）。

### Loop 组件（13 个）

| 组件 | 插件 | 工具（代表性） |
|------|------|------|
| `basic-tools` | Plugin.BasicTools | `speak`, `thinking`, `send_media`, `send_file`, `adapter_action` |
| `working-tools` | Plugin.WorkingTools | `pinboard`, `thinking_notes`, `retain_list`, `task_management`, `mark_for_review` |
| `scheduled-tasks` | Plugin.ScheduledTasks | `schedule_task`, `cancel_task`, `list_tasks`, `dismiss_notification` |
| `system-ops` | Plugin.SystemTools | `create_sub_agent`, `stop_sub_agent`, `send_instruction`, `list_sub_agents`, `wait_sub`, `get_sub_info` |
| `sub-agent-runtime` | Plugin.SystemTools | `report_to_parent` |
| `network-tools` | Plugin.NetworkTools | `http_request`, `download_file` |
| `browser-tools` | Plugin.NetworkTools | Playwright 浏览器自动化 10 个工具 |
| `ssh-tools` | Plugin.SshTools | SSH 工具 |
| `git-tools` | Plugin.GitTools | `git_*` 仓库操作 |
| `web-search` | Plugin.WebSearch | 网页搜索 |
| `group-file-tools` | Plugin.GroupFileTools | 分组文件操作 |
| `quick-actions` | Plugin.QuickActions | 快捷操作 |
| `home-assistant` | Plugin.HomeAssistant | `ha_get_states`, `ha_call_service`, `ha_search_entities`, `ha_list_devices`, `ha_trigger`, `ha_get_history` |

### Global 组件（19 个）

| 组件 | 插件 | 工具（代表性） |
|------|------|------|
| `memory-tools` | Plugin.MemoryTools | `memory_store`, `memory_get`, `memory_search`, `memory_list` 等（依赖 IMemoryAccess） |
| `file-tools` | Plugin.FileTools | `read_text`, `write_text`, `update_text`, `list_dir`, `move_file`, `delete_file`, `copy_file` |
| `file-ops` | Plugin.FileOps | `archive_create/extract/list`, `search_files`, `grep_files`, `file_info`, `file_hash`, `compare_files` |
| `review-tools` | Plugin.ReviewTools | 12 个 `review_*` 工具 |
| `email` | Plugin.Email | `send_email`, `check_unread`, `read_email`, `search_email` 等（9 个） |
| `skill-tools` | Plugin.SkillTools | `invoke_skill`, `list_skills`, `load_skill_file`, `list_skill_files` |
| `currency` | Plugin.Currency | 货币工具 |
| `campus-print` | Plugin.CampusPrint | 校园打印 |
| `image-tools` | Plugin.ImageTools | 图片读取与 OCR |
| `svg-tools` | Plugin.SvgTools | `render_svg` |
| `document-tools` | Plugin.DocumentTools | 文档处理 |
| `browser` | Plugin.NetworkTools | 浏览器自动化 Global 面 |
| `network-tools-global` | Plugin.NetworkTools | 网络工具 Global 面 |
| `ssh-tools-global` | Plugin.SshTools | SSH Global 面 |
| `git-tools-global` | Plugin.GitTools | `github_*` |
| `web-search-global` | Plugin.WebSearch | 网页搜索 Global 面 |
| `group-file-tools-global` | Plugin.GroupFileTools | 分组文件 Global 面 |
| `scheduled-tasks-timer` | Plugin.ScheduledTasks | 定时触发 Global 面 |
| `ha-global` | Plugin.HomeAssistant | Home Assistant Global 面（共享 HttpClient + HaConfig 配置） |

> 会话类型装配（哪些组件出现在 express/channel/task/review 会话）由 `Storage/SessionTypes/{type}.json` 的 `Components` 清单决定，插件自身不声明。

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
- 所有工具都已放入组件，不再有无组件独立工具模式

### 3. 工具元数据

```csharp
[ToolMeta(
    Group = "my-group",              // WebUI 分组
    ContinueLoop = true              // 执行后触发下一轮
)]
```

### 4. 获取宿主服务

组件内工具构造函数接收 `IPluginStorage`，通过 `context.GetService<T>()` 拿服务：

```csharp
// 组件 OnInitAsync 中
var memory = context.GetService<IMemoryAccess>();
var channel = context.GetService<IChannelAccess>();
var log = context.GetService<ILogAccess>();
```

常用服务：`IMemoryAccess`, `IChannelAccess`, `IEngineAccess`, `ILogAccess`, `IAdapterAccess`, `IPersonAccess`, `IReviewAccess`, `IBeaconAccess`, `IConnectedChannel`（完整清单见 [api-reference.md](api-reference.md)）

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
| Loop 组件在某个会话类型不出现 | 该类型 `SessionTypes/{type}.json` 的 `Components` 清单没包含它 | 在 `/p/plugins/types` 把组件加入该类型清单（插件已不声明引擎适用性） |
| Loop 组件 OnInit 里 `IConnectedChannel.CurrentChannelId` 恒为 0 | 组件装配发生在 AutoClaim 之前，此时会话尚未占频道 | 只注入 `IConnectedChannel` 引用，在 `ExecuteAsync` 里运行期解析频道/适配器（参考 `Plugin.BasicTools`） |
| 插件工具静默不注册 | 工具构造依赖 `adapterId` 等运行期值，`OnInitAsync` 提前 `return` 就整组不挂载 | 组件 `OnInitAsync` 只做无条件的构造，失败信息留到工具执行时返回 |
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
AgentLilara.PluginSDK/ComponentAttribute.cs     ← [Component]
AgentLilara.PluginSDK/ToolMetaAttribute.cs      ← [ToolMeta] / [PluginDependency]
AgentLilara.PluginSDK/Services/                 ← 17 个服务接口 + Product DTO

# 宿主侧（加载 + 生命周期）
AgentCoreProcessor/Tool/Host/PluginLoader.cs    ← 扫描 / 加载 / 热重载
AgentCoreProcessor/Component/ComponentRegistry.cs ← 组件类型注册表
AgentCoreProcessor/Component/ComponentHost.cs   ← Loop 组件宿主
AgentCoreProcessor/Component/GlobalComponentHost.cs ← 全局组件宿主

# 配置
Storage/SessionTypes/{channel,task,express,review}.json ← 会话类型档案（**唯一权限源**：Core/AllowedCores/Components/Tools 白黑名单/Prompts 自包含）
Storage/ToolConfig.json                                ← 全局工具禁用（kill switch）
```
