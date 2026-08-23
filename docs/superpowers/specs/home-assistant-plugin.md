# Home Assistant 插件设计

> 状态：**已实现** · 2026-08-23
> 决策：**全面集成**（连接/认证/Global组件同一套，空调是第一个用例）；工具 = 读状态 + 调服务 + 实体搜索/列表 + 自动化执行 + 历史查询

## 目标

把局域网内的 Home Assistant（`homeassistant.lan` → 192.168.123.65，**HAOS 2026.8.2**）接入 Agent，做成标准插件 `Plugin.HomeAssistant`，让 Agent 能在会话中：

- 查询实体/设备状态（`ha_get_states`）
- 调服务（`ha_call_service`，如 `climate.set_temperature`）——**空调等温控是首要用例**
- 按名称/域搜索实体、列出设备（`ha_search_entities`）
- 触发自动化/脚本（`ha_trigger`）
- 查历史（`ha_get_history`）

## 参考

形态对齐 `Plugin.SshTools`（Global 单例持连接 + Loop 每会话工具 + Static Accessor Bridge + 配置文件懒加载）与 `Plugin.NetworkTools`（HTTP 客户端 + 安全配置）。

## 架构

```
Plugin.HomeAssistant/
├── plugin.json
├── Plugin.HomeAssistant.csproj
├── HaConfig.cs                        ← Host/Port/Token/MaxOutput 等，SshConfig 同款懒加载
├── HaGlobalComponent.cs               ← Global 单例：HttpClient 共享 + 懒连接 + config
├── HaToolsAccessor.cs                 ← Static Accessor Bridge（Global → Loop）
├── HaLoopComponent.cs                 ← Loop：按会话注册工具 + BuildPromptSection 注入屋况概览
└── Tools/
    ├── HaGetStatesTool.cs             ← ha_get_states
    ├── HaCallServiceTool.cs           ← ha_call_service
    ├── HaSearchEntitiesTool.cs        ← ha_search_entities
    ├── HaListDevicesTool.cs           ← ha_list_devices
    ├── HaTriggerTool.cs               ← ha_trigger
    └── HaGetHistoryTool.cs            ← ha_get_history
```

### Global 组件（`ha-global`）

- `[Component(Name="ha-global", Scope=ComponentScope.Global)]`，`GlobalComponentBase`
- `OnInitAsync` 中：
  - `_config = HaConfig.Load(context.Storage)`（放 `Storage/PluginData/ha/`）
  - 建共享 `HttpClient`（BaseAddress 由 config 决定）
  - `HaToolsAccessor.Configure(this)`
- **懒连接**：不在启动时 ping/认证；工具首次调用时 `EnsureReady()` 校验 config 有效性（token 非空、host 可达）
- 无后台任务 → 不实现 ISessionResource（HA 调用都是同步/短 HTTP，不需要异步任务注册和唤醒）

### Loop 组件（`home-assistant`）

- `[Component(Name="home-assistant", Scope=ComponentScope.Loop)]`，`LoopComponentBase`
- `OnInitAsync`：`var global = HaToolsAccessor.Global ?? throw ...`，实例化 6 个工具
- `BuildPromptSection()`：返回一则**屋况概览提示**（当前实体快照摘要，如各空调目标/室温、开机设备数），引导模型在合适时主动查询；空则返回 null，避免常驻噪音
- `OnCanExitAsync()`：无异步资源，直接 true

### 工具（全部 `[ToolMeta(Group="ha", ContinueLoop=true)]`，参数名英文）

| 工具 | 参数 | 说明 |
|------|------|------|
| `ha_get_states` | `entity_ids?/domain?` | 查一个/多个实体状态，或按域返回全部。输出截断 + 汇总 |
| `ha_call_service` | `domain`, `service`, `entity_id?`, `data?`(JSON) | 调任意服务。**写操作核心** |
| `ha_search_entities` | `query` | 按 friendly_name/entity_id 模糊搜索，返回匹配项 |
| `ha_list_devices` | `domain?` | 列出设备（含 state/attribute 摘要） |
| `ha_trigger` | `entity_id`（script./automation.） | 触发自动化/脚本 |
| `ha_get_history` | `entity_id`, `hours?/period?` | 历史状态查询，返回时间序列摘要 |

### 配置（`HaConfig.json`）

放 `Storage/PluginData/ha/HaConfig.json`（GitHub 仓库**不收录**，配置模板源 `Storage/SessionTypes/` 同理需同步两处——本项目用 `ipconfig` 同款方案：`Storage/PluginData/` 在仓库外）。

- `Host`（默认 `homeassistant.lan`）
- `Port`（默认 `80`——**实测 8123 关闭、80 反代开放**，config 里默认 80）
- `Token`（**长期访问令牌**，从 HA 生成；不默认、不硬编码）
- `UseHttps`（默认 false）
- `MaxResponseBytes`、`TimeoutSeconds`

### 安全/可逆性

- Token 只在 config 文件，不进 plugin.json、不进代码、不进 git
- 所有写操作走 `ha_call_service` / `ha_trigger`：模型需明确指明 domain/service/entity_id；HA 侧自身有权限模型
- 输出截断，避免过量注入
- 不提供"无条件执行任意 HA 服务"的逃逸口——`ha_call_service` 参数即模型意图，服务是否危险由模型判断 + 用户指令监督（与 SSH 工具同一哲学）

### 会话类型接入（唯一权限源）

`Storage/SessionTypes/{type}.json` 的 `Components` 清单决定装配。默认加到 **channel**（working 频道会话）等会话：

- `channel.json`：`Components` 加 `"home-assistant"`
- `express.json` / `task.json` / `review.json`：按需（可用 `ha_*` 工具；express 轻量会话可不装配）

> ⚠ 需**三处同步**：`Storage/SessionTypes/`（模板源）+ `AgentCoreProcessor/templates/`（模板备源）＋ 运行期 `E:/Tools/Lilara/Storage/SessionTypes/`（若发布过）。见 memory `project-qq-forward` 记录。

## 配置模板 vs 运行期

- 插件 DLL、plugin.json → `Plugins/Plugin.HomeAssistant/`（build 自动复制）
- `HaConfig.json` → **运行期** `E:/Tools/Lilara/Storage/PluginData/ha/`（发布用）；开发期 `Storage/PluginData/ha/`
- `SessionTypes` 三处同步

## 验证

1. `dotnet build`（整解决方案）
2. `--dev` 起 HttpBridge(9876)+WebUI(5000)：signal 日志查 `插件已加载 home-assistant`；会话 components 含 `home-assistant`
3. `curl POST /api/send` 在 channel 会话里问"现在客厅空调几度"→ 应触发 `ha_get_states` 并返回
4. 调 `ha_call_service` 把空调调低 1°C，回读验证
5. 真实会话端到端由用户在当前会话实测（本轮已手工验证过 API 连通与调温）

## 开放点（实现前定）

1. `BuildPromptSection` 的屋况快照：每次调用都拉 /api/states 太重。**方案**：缓存 30s + 首轮注入；或只注入"可用工具说明"不注入实时数据。倾向**轻量：只注入工具存活性 + 最近一次已知状态摘要**，实时查证交给模型调 `ha_get_states`。
2. 是否给 `express` 会话也装配（轻量会话常驻 HA 工具 vs 只让 channel 用）。倾向：**只 channel**，避免 express 误触发写操作；express 需要时经 escalate 升 channel。
3. Token 是否需要支持多实例（多台 HA）。首版**单实例**，config 单份。