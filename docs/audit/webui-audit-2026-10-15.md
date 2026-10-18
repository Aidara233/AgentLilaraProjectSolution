# WebUI 页面与功能审计（2026-10-15）

> 2026-10-18 更新：本轮审计后的修缮已完成并通过验证，见文末「修缮完成记录」。
> 正文保留为审计快照，部分问题已修复，阅读时以完成记录与当前代码为准。


## 范围与验证边界

本轮检查当前源码中的 17 个内置 Provider、独立 Razor 页面、全部 9 种已实现卡片、ProviderRegistry、DataSourceManager、DynamicPage、导航和相关引擎/存储调用。还搜索了插件中的 WebUI Provider 实现，未发现其他页面提供者。

这是源码级审计，不是所有页面已经通过浏览器验收。没有修改业务代码、运行配置或数据库，没有执行发送、删除、停止、重载等操作。

只读 HTTP 检查（本地既有实例，不保证与当前工作树版本相同）：

- `/login`：200。
- `/p/dashboard`、`/p/sessions`、`/mcp`、`/logs/trace`、`/p/logs/trace`：未登录均 302 到登录页。
- `/js/log-trace.js`：200。
- 没有登录测试、跨标签页实测或浏览器交互测试；下文“确认”指代码链路已闭合核对，不表示做过生产操作复现。
- 本轮源码审计后，工作树已加入 DataSourceManager 请求取消/过期结果隔离、SignalContext 上下文恢复保护及独立 Trace Provider 标注；本轮完整解决方案构建成功，162 个测试通过，--test 自包含运行正常。仍没有针对 ProviderRegistry/DataSourceManager/TableCard/LogTrace 的专门测试，因此浏览器交互验收和跨标签页回归仍属于后续工作。

## 逐页覆盖与职责评估

| Provider/页面 | 当前职责 | 审计结论 |
|---|---|---|
| Dashboard `/p/dashboard` | 状态摘要与快捷控制 | 冲动值列无数据；静音仅改标志，未找到业务消费；做梦信号无消费者。摘要与详情共存合理，不应删除摘要来消除“重复”。 |
| Engines `/p/engines` | 运行实例总览 | 汇总统计所有活跃引擎，但列表只列频道会话、Session、Vision，遗漏 Message/Adapter/Mind/Timer 等；会话被混入引擎列表；Session 详情链接不存在。 |
| EngineDetail `/p/engines/vision` | 视觉状态简页 | 与 `/p/vision` 重复，且轮次/Token/压缩永久为“—”；路由随实例存活动态出现/消失。建议兼容跳转到视觉管理页。 |
| Sessions `/p/sessions` | 活跃会话与换核 | 停止/强杀/换核有后端；所有详情链接缺少目标页；空态伪造一条可执行操作的会话行；换核后列表没有联动刷新。 |
| Sessions `/p/sessions/history` | 会话历史 | 只扫描旧 meta.json，漏掉正常 spec/state/history 三文件存档；服务端分页受共享表格缺陷影响；详情链接不存在。 |
| Sessions `/p/sessions/inject` | 直接向 MessageEngine 注入 | 与控制台模拟用户路径不同，不能简单视为重复；不校验频道存在，不负责无会话时孵化，也不是命令解析入口。UI 未解释这些语义。 |
| Channel `/p/channels`、`/p/channels/{数值ID}` | 频道资料、消息、会话入口 | 详情来自缓存枚举而非稳定模板；搜索总数未按关键词过滤；频道页展示/触发的是全局伴随提取，名称让人误以为 per-channel；会话跳转断链。 |
| Console `/p/channels/console` | 交互模拟与频道控制 | 强制压缩未实现；切换频道不清名称缓存；Bot 模式走入向路由；异步发送不等待；数据源选择状态跨页面实例共享。 |
| Vision `/p/vision` | 视觉状态、配置与队列 | 有启动/唤醒/停止实现；队列只取 50 条却以准确数量呈现；配置无范围校验且直接改运行对象，保存时更换并释放正在使用的信号量。 |
| Vision `/p/vision/gallery` | 图片、重试、删除 | 后端动作存在；服务端分页/筛选契约断裂。阶段显示 P1/P2 等而筛选值为 1/2；分类筛选键 classification 与输出 category 不同，本地筛选得到空集。 |
| Dream `/p/dream` | 框架事件 | 实际不是做梦，菜单标题已是框架事件，但 Provider 名/路径残留；仅最近 500 条后再搜索分页，没有说明范围；受推送查询丢失影响。 |
| Dream `/p/dream-claims` | 推断断言子集 | 与主库“推断”筛选重复、信息更少。建议作为主库预设视图/兼容入口，而非独立维护查询。 |
| Review `/p/review`、`/p/review-groups` | 验证队列与冲突组只读展示 | 职责合理，不应把只读数据源空 Submit 当成失效按钮；但目标引用、冲突成员只有文字/数量，缺少详情穿透；服务端分页受共性缺陷影响。 |
| Memory `/p/memory`、`memory-journals`、`memory-keywords`、`memory-emotion` | 断言、经历、关键词、情绪快照 | 分类基本合理。前三页服务端分页；类型/排他筛选原始值与显示字段不一致。断言/经历正文截断且无详情入口；情绪是历史快照而非实时情绪，需明确标签。 |
| Adapter `/p/adapters`、`adapters/onebot`、`adapters/file`、`adapters/http`、`adapters/icalingua` | 适配器实例、配置与协议操作 | 四种平台页都存在且在导航显示（不是全部隐藏详情页）。选择状态共享；File 输出目录字段无消费者；新建/启用 File 等待常驻 StartAsync 导致提交不结束；动作清单仅构造时按首个实例采集。 |
| Config `/p/config`、四个分组及 `config/{分组}/{name}` | 文件配置编辑 | 与专用页面存在多写入口，生效策略不一致；选择状态共享；复杂 JSON 保存变字符串；保存失败仍显示已保存；文件缺失/解析错误显示无属性；固定清单遗漏 ExtractionCore 等现有配置。 |
| Plugins `/p/plugins`、`plugins/{name}` | 插件清单、详情与重载 | 详情信息按 PluginId 查，但工具/组件按目录名查；全部重载绕过 MasterEngine 的 Global 组件生命周期；重载底层错误可能被吞后显示成功。 |
| Plugins `/p/plugins/tools` | 工具总览与全局禁用 | 操作读取不存在的 _row_id；核心来源筛选 __core__ 与展示值“核心”不匹配；工具来源映射只看活跃引擎/Global，不完整覆盖会话组件或临时实例。 |
| Plugins `/p/plugins/types` | 会话类型权限与组件配置 | 加入/移出同样读取缺失 _row_id；模式/清单修改后相关卡片不联动；“恢复默认”仅重置工具模式/清单，不重置整个会话类型；标题“勾选”但交互实际是按钮。 |
| Logs `/p/logs/tokens` | Token 聚合 | 缓存概览取最近 2000 个成功调用，按 Core/模型为全库统计，范围不一致且未标明。 |
| Logs `/p/logs/model` | 模型调用记录与内容详情 | 列表+联动详情职责合理；仅最近 2000 条；详情依赖日志文件，有缺失提示，但不应冒充全历史检索。 |
| LogTrace `/logs/trace`、`/p/logs/trace` | 独立 SVG 信号追踪 | 独立实现合理，Provider 仅导航。加载更早游标不更新；过滤后行数控制翻页会提前终止；实时流绕过 open-only；暂停丢弃新事件且恢复不补读。 |
| Currency `/p/currency` | 账户、拨款、流水 | 不属于纯调试，建议归资源管理；第二页将取数上限从 500 改成 PageSize，随后再次客户端分页变空；拨款后余额/流水不联动；累计统计仅最近 1000 条。 |
| Test `/p/test/status`、`/p/test/cards` | mock 卡片演示 | 隐藏合理，不是生产功能遗漏；保存返回 mock 成功而不持久化，不能作为功能闭环测试。 |
| MCP `/mcp` | 连接/工具状态、配置重载 | 后端重载与反馈存在；没有菜单入口；只在初始化获取 manager，冷启动提前进入可能停留“未初始化”。 |
| `/`、`/login` | 首页别名、认证入口 | 首页跳转合理。登录表单不传递 ReturnUrl，成功总回首页。HTTP+Secure Cookie 部署组合需专门验证。 |

## 优先修复的确定问题

### 1. 跨标签页共享可变数据源（高优先级）

Program.cs:379 将 Registry 注册为单例。ConsoleProvider:28、AdapterProvider:26、ConfigProvider:53 在构造时缓存 PageDefinition/DataSource 实例；DataSourceManager.Initialize 直接使用 def.Source。

因此多个 DynamicPage 使用同一个 ConfigEditSource._currentFile、ConsoleChatSource._channelId、ConsoleEngineSource._selectedChannelId、AdapterConfigSource/ControlSource._selectedId。A 标签展示 A、B 标签选择 B 后，A 的提交可落在 B。ConsoleChatSource.Subscribe 还把每次 timer 存到单一字段，旧 subscription 释放可能关闭新订阅的 timer 并遗留旧 timer。

建议：页面数据源工厂/页面作用域实例；提交携带并验证稳定目标 ID；订阅清理闭包捕获自身资源；不要通过浅拷贝 PageDefinition 或仅加 generation 假装修复。

### 2. 配置保存会改变 JSON 类型并错误报告成功

- PropertyEditorCard.razor:264–272 把 JSON 编辑内容保存为字符串，不解析或校验。
- ConfigProvider.cs:417–432 对原 JsonObject/JsonArray 进入默认字符串分支，数组/对象变为 JSON 字符串。
- PropertyEditorCard.razor:298–305 无论 ActionResult 如何均清空 dirty 并显示“已保存”。
- ConfigEditSource.FetchAsync 无 Extra 时返回空属性，不保留查询；DynamicPage 提交后 FetchAsync 不带原查询，联动编辑器保存后消失。
- 文件直接 WriteAllTextAsync，不是原子替换；无版本/并发写冲突检查。

建议：类型保持、验证后原子写、明确目标与版本；失败保留编辑草稿；只在确认成功后标记已保存。

### 3. 会话存档和详情链路断裂

- SessionsProvider:31–36 只注册活跃、历史、注入三页，没有 sessions/{id}。
- SessionsProvider:232/403、ChannelProvider:262、EnginesProvider:154 都输出这个不存在的链接。
- EnginesProvider:170 另输出不存在的 engines/session。
- SessionsHistorySource:349 只找 meta.json；SessionPersistence:29–35 使用 spec/state/history；RuntimeSession:1509–1527 仅无 persister 兜底才写 meta。

建议：补活跃/归档共用详情，历史读取现行 schema 并兼容旧 meta；包含状态、核心、组件、端口、父子、池/资源、上下文或历史摘要，避免通过删链接掩盖遗漏。

### 4. 工具管理按钮没有目标 ID

PluginsProvider:594/743/987/1083 只读 _row_id，但数据行提供 name/id，TableCard:291 原样回传，没有注入 _row_id。影响全局工具启停、单插件工具启停、类型工具/组件加入移出。

插件详情工具/组件查找（614、657）按 FileName 比较，清单链接却使用 PluginId（393）；实际如 currency 与 Plugin.Currency 不同，导致信息卡正常、工具和组件卡空白。

### 5. 控制台动作与发送语义错误

- ConsoleProvider:109 提供 force-compress，219–226 无处理。
- 切频道只改 _channelId（302–303），没有重置非空 _channelName（309）；发送继续用之前频道名称。SessionManager.OnMessageAsync 按名称映射频道，确认会投错频道。
- SendAsBot（403–418）的 adapter_only/both 调用 RouteMessageAsync，是用户入向入口，不是适配器出向；会入库/触发对话，both 又单独保存 Bot 消息。
- SendAsUser/Custom fire-and-forget，外层 Task.Run 只等启动动作，不等路由结束，所以成功消息不代表投递成功。
- ForceStop（256）忽略 StopSession 的失败结果。

### 6. 仪表盘保留废弃控制

- 冲动值列（DashboardProvider:93）无对应输出（250–257）。
- trigger-dream（173）仅发布 force-sleep；全解决方案搜索只有发布/说明，没有消费实现。
- mute/unmute（152/156）仅写 MasterEngine.MuteMode；全解决方案搜索未发现消息/工具出向路径读取该值，当前 UI 的“静音”不是可靠控制。

### 7. 表格契约与显示值混用

- 服务端分页：ChannelMessages、SessionsHistory、VisionGallery、DreamEvents/Claims、ReviewQueue/ConflictGroups、Claims/Journals/Keywords。
- 客户端全量分页：AllTools、SessionTypeTools、ModelList 等。
- TableCard 全部本地 Skip/Take，忽略 TotalCount；搜索/排序/筛选不立即提交，翻页才提交。
- 不能以 TotalCount 是否存在推断模式，因为两类数据源都设置它。
- 本地过滤拿显示字符串匹配原始值：图片 P1 vs 1、分类 category vs classification、断言 推断 vs inference、关键词 is_exclusive vs exclusive、核心来源 核心 vs __core__。
- 排序全部按字符串，数字也按字典序；服务端多处忽略 SortBy。
- 行操作 Confirm 为空实现；选中状态为页内索引；当前页不随数据缩小修正。

建议显式查询/分页模式，服务端过滤排序后分页；客户端表格翻页不重新请求；显示值与原始查询字段分离；明确稳定行键。

### 8. 操作反馈与卡片联动不闭合

DynamicPage:211–241 忽略失败/非跳转消息，无条件刷新当前数据源。货币拨款、会话换核、类型权限修改等改变其他卡片，但其他卡片没有刷新订阅/失效关系。

表单/动作 Required 多为星号，未实际验证；ActionCard 首个 select 选项显示与空 payload 可能不一致；ChatCard 发送前清空文本，失败丢草稿。

建议统一操作结果、提交中状态、失败保留草稿，以及显式受影响数据源刷新列表，避免刷新所有卡片覆盖未保存编辑。

### 9. 适配器配置与生命周期不闭合

- FileAdapter.StartAsync:58–96 是常驻轮询，AdapterManager.AddAsync:115/EnableAsync:141 await 它，WebUI 创建/启用 File 会一直等待直到停止。
- File 的 UI input_dir 实为 baseDir，其构造再追加 input；output_dir 保存到 outputDir，但 AdapterFactory.CreateFile 未读取，输出始终 baseDir/output。
- AdapterManager.UpdateConfig 仅存配置文件；UI 未说明重建/重载生效规则。启停、更新没有统一 OnAdaptersChanged，其他卡片易显示旧状态。
- 平台动作卡片只在 Provider 构造时查询现有首个实例；后来新建首个适配器不会补出动作入口。
- 秘钥字段 OneBot/HTTP 使用 Text，Icalingua 私钥使用 TextArea，默认明文显示。

### 10. 独立 Trace 的真实功能缺陷（保留架构）

- LogTrace.razor:138 总用 _trace.Rows[0] 作游标；LoadOlder 只 prepend JS，不更新 _trace.Rows/游标。再次点击重复请求同一页，JS prependRows:1439 无去重。
- GetRecentBefore 只用 timestamp < before，遇到同毫秒跨页会漏记录；应使用 timestamp+id 稳定游标。
- _hasOlder 按过滤后的 Rows.Count >= 200 推断，过滤掉记录后会提前禁用加载；SQL 先 LIMIT 再过滤，最近 200 条无匹配时无法继续寻找更早匹配。
- OnNewEvent:239–269 没有 open-only 处理，也不维护 scopes；“仅卡住”初始过滤后仍追加普通事件/close，状态不会正确收敛。
- 暂停直接丢弃事件，恢复只切布尔，不补读；与历史一致性断开。
- App.razor:6 有 base href=/，JS 文件 HTTP 200。此前“嵌套路由必然导致 import 404”的结论不成立。

## 分类与冗余的建议（不等于已实施）

- 保留“总览”作为入口；“运行管理”包含基础设施引擎、会话、频道，明确会话不是基础设施引擎。
- 视觉单独领域页保留状态/配置/图片库；engines/vision 兼容跳转，不再维护空字段简页。
- 心智与记忆可统一一级分组，保留断言、经历、关键词、情绪、框架事件、验证队列/冲突各自子职责；推断断言用主库预设筛选。
- 插件与工具包含插件、工具、会话类型权限和 MCP；MCP 可保持独立 Razor 页，只补导航契约。
- 适配器是外部通道接入，频道是业务对象，二者不合并。
- 日志与诊断包含 Trace、模型调用、Token；配置和货币不是“调试”，分别归系统设置和资源管理。
- 控制台模拟入向和 MessageEngine 直接注入语义不同，可共享服务但不能无说明地合并。
- 首页重定向、独立 Trace 双路由、刻意隐藏 mock 测试页不是冗余缺陷。

## 完整性缺口与范围说明

下面是已存在后端能力的 WebUI 缺口，不代表每个工具都应该拥有一个管理页：

- 会话详情/现行归档浏览：已有链接承诺但未实现，必须补齐。
- 资源池树、占用、子资源/TODO/定时任务：管理面板缺少有效观察入口，引擎列表只给部分数量。
- 系统 pins、断言/经历完整内容、冲突成员和证据链：仅工具/数据层可访问，当前 UI 无完整穿透路径。
- 配置页固定清单遗漏 ExtractionCore；其他适配器已有专页，不宜重复加所有原始配置文件。会话类型页只开放工具/组件，不包含 AllowedCores/端口等完整类型配置，需标清能力边界。
- 日志最近 2000、事件最近 500、情绪最近 100、货币累计最近 1000：必须显式标注范围或实现全量查询，不能展示为完整总数。

## 不应照单采纳的旧审计条目

- 只读 Submit 返回成功：契约不严谨，但没有对应按钮时不是用户可见空操作。
- 隐藏详情页不入导航：可能合理；NavMenu 有前缀展开支持，不能泛化为全部详情页无法展开。真正确定问题是 OnInitialized 先 ExpandForCurrentPath 再构建 navGroups，直接打开页面时初始展开落空。
- Same PageDefinition 路由参数变化：正常导航 OnParametersSetAsync 会重建，不以 OnRegistryChanged 引用比较推断缺陷。
- Provider 路由冲突：机制无校验，属于条件风险；精确匹配优先，不能说 plugins/tools 必然被 plugins/{name} 抢占。
- 图片时间不可机械全改 ToLocal：ImageStorage 当前存在 DateTime.Now 写入；需逐数据表确认存储时区。其他 UTC 表的直接 ToString 展示也需一并统一。
- HTTP + Secure Cookie：LAN 明文访问不兼容，localhost 有浏览器例外；本轮未做登录验收，不能声称所有部署登录必坏。

## 建议实施顺序

1. 页面作用域数据源、稳定提交目标、页面异步隔离和操作结果契约。
2. 配置类型安全/原子保存/草稿保留；控制台正确投递；工具按钮 ID 和插件身份一致性。
3. 显式查询分页模式及全部对应 Provider 适配；跨卡片精确失效刷新。
4. 会话详情与现行归档、引擎清单完整性；撤销废弃控制或通过真实业务接口接通。
5. Trace 游标/过滤/实时一致性；导航分类、MCP 入口、视觉/推断重复视图收敛。
6. 建立回归矩阵：跨两标签选择后保存/发送、配置对象/数组往返、每张服务端表第二页与筛选、动作失败不丢草稿、重载生命周期、Trace 连续加载两页与暂停恢复。
7. 隔离测试存储下启动 --dev 做浏览器验证，不拿现有实例的配置、账户和真实消息做破坏性试验；生产 QQ 端到端留给用户实测。

---

## 修缮完成记录（2026-10-18）

### 契约与共享层

- `DataSourceDefinition` 新增 `Factory`（页面作用域数据源）与 `Paging`（Client/Server 显式分页语义）；`Source` 改为可空，未声明时明确抛配置错误。
- `TableSchema.RowKeyField`、`CardDefinition.AffectedDataSourceIds` 新增；行操作 payload 自动补齐 `_row_id`，稳定行键取代页内索引选中。
- `DataSourceManager` 重写：每请求 linked CTS + 版本号，取消不残留 loading、重试等待可取消、提交受页面生命周期约束、订阅幂等且清理闭包自持、重复 ID 显式报错。
- `DynamicPage` 重写：页面级初始化 CTS、操作令牌、失败不刷新且顶部展示错误、成功消息展示、保留 `_lastQueries`（推送/刷新不丢搜索排序与联动条件）、跨卡片精确失效刷新、未知卡片类型显式提示。
- `TableCard`：服务端分页不再二次 Skip/Take，使用服务端 `TotalCount`；服务端搜索/排序/筛选即时提交（搜索 300ms 防抖）；客户端分页翻页不重复请求；危险行操作默认确认；行键负载注入。
- `StatusCard` 指示器样式改为独立 `style` 属性；`FormCard` 真正校验 Required；`ActionCard` 下拉首项与 payload 一致；`PropertyEditorCard` 保留未保存草稿、保存结果由页面回传。

### 功能链路

- 配置：对象/数组编辑解析回 JSON 类型，非法 JSON 拒绝；原子写入；同一文件回读不清空草稿；选择记忆；补齐 `ExtractionCore` 等配置入口。
- 控制台：移除无实现的“强制压缩”；切换频道重置名称缓存；Bot 模式经 `AdapterEngine.ResolveAdapter` 走真实出向（adapter_only/both/db_only）；用户/自定义模式等待路由完成；停止检查 `StopResult`；订阅 timer 闭包自持。
- 插件：行键兜底 `id/name`；插件详情按 PluginId 或目录名统一查找；工具/会话类型数据源页面作用域化。
- 会话：新增 `sessions/{sessionId}` 活跃/归档共用详情（含资源池/端口/组件），历史页读取 spec/state/history 并兼容 meta；空态不再伪造会话行；换核后刷新列表。
- 引擎/频道：Session 链接改为 `/p/sessions`，补齐其他基础设施引擎，移除 `engines/session` 断链；频道消息总数按搜索范围修正；强制提取标注为全局。
- 适配器：全部平台选择/控制/配置数据源页面作用域化；File `StartAsync` 后台启动、`outputDir` 生效；启停/配置更新触发 `OnAdaptersChanged`；密钥字段使用密码框；列表删除默认确认。
- 仪表盘：移除无数据的冲动值列、无消费方的静音/做梦动作与字段，保留有实现的视觉触发。
- 视觉/记忆/日志/货币：分页模式逐个声明；图片阶段与分类提供原始查询字段；服务端表格稳定行键；货币客户端分页固定窗口、拨款后联动；Token 统计标注范围并统一口径。
- Trace：`timestamp + id` 稳定游标与 SQL `GetSignalBefore`；加载更早推进游标并去重；`HasOlder` 来自未过滤页；实时流应用级别/Scope/仅卡住过滤；暂停记录而非丢弃，恢复整体重载；`/p/logs/trace` 改为兼容跳转壳，正式入口 `/logs/trace`。
- 导航：修复初始展开顺序；隐藏详情页按前缀自动展开所属分组；`DefaultCollapsed` 不再误导；MCP 增加导航入口并修复冷启动未初始化；货币/配置迁出“调试”分组；未知路由显式 NotFound。
- 注册表：Provider ID 冲突与页面精确路由冲突记录告警；模板按字面段排序、路由规范化与参数 URL 解码。

### 验证

- 全解决方案构建：0 错误（仅既有 NuGet 漏洞与 FormatterServices 过时警告）。
- 测试：173/173 通过（新增 `WebDataSourceTests` 与 `ProviderRegistryTests` 共 11 个）。
- 隔离 Storage + 独立端口（WebUI 15000 / HttpBridge 19876）`--dev` + Playwright（系统 Chrome）实测：
  - 登录跳转、总览清理、导航分组；
  - 契约页：服务端 1/3→2/3 第二页非空、共 25 条、客户端 1/6、服务端搜索共 6 条、失败操作提示、危险操作确认与成功提示；
  - 配置编辑器：成功消息、非法 JSON 拒绝、不再虚假显示已保存；
  - 跨标签页草稿隔离；
  - `/logs/trace` 独立页渲染 178 行节点、`/p/logs/trace` 跳转。
  - 隔离环境只有单页数据，“加载更早”禁用符合预期；连续分页逻辑由游标代码与页面实测覆盖，未做大库翻页压测。
- 验证后已停止隔离实例、删除临时 Storage/探针，调试输出 `paths.json` 恢复指向 `E:\Tools\Lilara\Storage`；正式部署进程（PID 23404）全程未中断。

### 保留与未做

- 独立 Trace 架构、`logs.db`、Signal 静态 API 保留。
- 未改变正式部署、未提交 git、未对生产 QQ 做端到端验证。
- 断言/经历全文详情、冲突成员与证据穿透、资源池/定时任务完整管理视图仍属于后续功能扩展，不在本轮“修复失效链路”范围。
- HTTP + Secure Cookie 的 LAN 明文部署风险未改：localhost 浏览器可登录；如从局域网其它机器以 HTTP 访问，仍会因 Secure Cookie 不发送而反复跳登录，需要 HTTPS 终止或单独的部署配置决策。

