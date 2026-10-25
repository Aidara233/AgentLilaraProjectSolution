# ALP → ALC 内核统一：边界 ADR 与迁移基线

- 状态：已批准方向；S0 基线冻结完成；S1a 内核库/CLI 分离完成（待后续包化）
- 日期：2026-10-20
- 目标：以 ALC 的 canonical 时间线与 Stepper 为模型执行内核，ALP 保留频道、插件、Mind、WebUI、SQLite 和会话调度宿主。
- 范围：只记录迁移原则、已知基线和阶段门槛；不代表后续阶段自动获批。

## 决策

### D1. 分层归属

- `Lilara.Core` 只负责 canonical 会话记录、提示槽位、上下文投影、模型协议转换、工具执行机制、压缩/回收原语与恢复。
- ALP 保留进程生命周期、`MasterEngine`/`SessionEngine`/`SessionPool`、频道 claim 和权重激活、适配器、消息路由、SQLite 业务数据、Mind、WebUI、组件生命周期及插件加载。
- 跨边界代码只放在 ALP 侧的 `AgentLilara.KernelBridge`（暂定名）。Core 不引用 ALP、ALP SDK、SQLite、QQ/频道类型或 WebUI 类型；只有 Bridge 依赖双方契约。
- 本 ADR 不决定必须合并仓库，也不批准把 ALP 组件/SpawnCheck/资源池搬入 ALC Kernel。

### D2. 单写者与存储隔离

- Legacy 会话的模型面历史仍由 ALP `SessionPersistence` 写；Kernel 会话的模型面历史由 ALC `SessionStore` 写。
- 一个会话任一时刻只能有一个模型面历史写者。禁止双写、自动双向同步或启动时隐式导入。
- ALP `state.json` 保持宿主生命周期/频道/资源状态的权威；ALC `state.json` 只承载内核的 turn/ask/step 状态。二者不是可互换的状态文件。
- Kernel 数据使用独立数据根，部署到 ALP Storage 下的独立子目录；旧数据在显式迁移决策前只读保留。
- 运行会话创建时固定 engine 与内核/格式版本；不得因热配置在同一会话中途改换执行引擎。

### D3. 执行边界

- Bridge 直接驱动 ALC `Stepper.StepAsync`，不调用 ALC CLI `CommandDispatcher` 的自动续跑策略；是否继续由 ALP 宿主依据现有 wait/end_session/compact/escalate/OutputOnly 语义决定。
- 真实输入在调用 Stepper 前先写成 canonical 时间线事件。
- ALP 工具继续在 ALP 进程中执行。ALP `ITool` 到 ALC `ITool` 的适配必须显式处理 JSON 参数映射、权限、错误、超时、取消及附件；不确定的映射失败关闭，不默默丢字段或伪报成功。
- ALP 工具副作用由宿主 dispatcher 执行；Core 不认识 `SessionEngine`、频道 claim、Mind、货币等业务服务。
- ALP compact 是新 session 接管，ALC compact 是同一 session 的持久检查点投影。初始迁移保留 ALP 编排；ALC 自动压缩/回收另行评估，不双重启用。

### D4. 运行时与项目边界（2026-10-20 确认）

- ALP 主程序与测试目标 net10.0；`LilaraConsoleClient`、插件 SDK 与不直接引用主程序的插件保持 net8.0。
- 插件若必须直接引用 `AgentCoreProcessor`（当前只有 `Plugin.WorkingTools`），必须与主程序同目标框架；这类依赖在整体迁移完成前应逐步收敛为 SDK 契约。
- 插件复制目标路径必须跟随主程序 TFM（`Directory.Build.props` 的 `HostPluginsDir`），不能硬编码 net8.0。
- 任何 net10 兼容性告警（安全公告、短读、过时 API）独立治理，不与内核迁移混为“无回归”。

## 当前仓库与基线

### ALP

- 仓库：`E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution`
- 基线提交：`1738e765629f8b3df01fbed83dd0720f7c1c9f49`
- 工作树：基线检查时无未提交改动；本地 `master` 相对 `origin/master` ahead 21，保留现状，不重置或切分支。
- S1b 的 net10 宿主兼容探测已完成（2026-10-20）：`AgentCoreProcessor` 和测试项目切到 net10 后，ALP 测试 173/173 通过；整解决方案 build 0 error。`Plugin.WorkingTools` 因直接引用 `AgentCoreProcessor` 同步切到 net10；其余插件与 `AgentLilara.PluginSDK` 保持 net8，全部成功编译并复制到 `AgentCoreProcessor/bin/Debug/net10.0/win-x64/Plugins/`。`Directory.Build.props` 的 HostPluginsDir 已同步改为 net10 路径。
- net10 探测期间修复一处 C# 14 兼容性：`WebUI/Components/Cards/TableCard.razor` 的 lambda 解构变量 `field` 改名为 `fieldName`，行为不变。当前保留 5 个告警：SQLitePCLRaw.lib.e_sqlite3 2.1.6 的 NU1903 高危公告、PluginLoader 的 CA2022 短读分析警告、测试 FormatterServices 的 SYSLIB0050（以及对应重复包告警）。这些不是本次内核迁移功能变更，但发布前必须单独治理。
- 隔离 `--dev` smoke：使用临时 `paths.json`/Storage 和端口环境启动 net10 主程序，成功启动 HttpBridge、WebUI 及 Message/Timer/Session/Vision/Mind 引擎，未连接外部 QQ；隔离进程已停止，正式 Storage 未使用。默认 Signal.Debug 不打印插件逐条加载，因此本轮只把插件产物布局与宿主启动作为通过项，不声称逐条运行时插件计数。
- 基线测试：`AgentCoreProcessor.Tests`，173/173 通过（2026-10-20，本地 `dotnet test --no-restore`）。
- 执行入口：`RuntimeSession.RunAsync` 将常驻 `_history` 直接传给 `AgentCore.InvokeAsync`；宿主的消息/频道/组件状态与真正对话事实共用旧 `Message` 历史。

### ALC

- 仓库：`E:/Workspace/AgentLilaraCode`
- 基线提交：`83ecfd6`（重试链路）；S1a/S1b 的包化改动以该提交之后的 ALC 工作树为准。
- `Lilara.Core.csproj` 已拆为 net10.0 class library，CLI 入口和 `CoreHost` 组合根现在位于 `src/Lilara.Cli`。
- S1b 起 `Lilara.Core` 具备固定包身份：`PackageId=Lilara.Core`、`Version=0.1.0`；本地 feed 由 `dotnet pack src/Lilara.Core/Lilara.Core.csproj -c Release -o artifacts/packages` 产出 `Lilara.Core.0.1.0.nupkg`（`artifacts/` 已 gitignore，包按版本固定、不从工作树漂移）。
- S1b 新增公开宿主门面 `Hosting/KernelRuntime`：`Open(dataRoot, workingDirectory)` 自持 `DataRootLock`，提供 `CreateSession` / `LoadSession` / `StepAsync`（先落用户消息再调模型）/ `ContinueAsync`（无新输入的工具续跑）/ `ReadTimeline`，并把同会话推进串行化。宿主不再需要 `CommandDispatcher` 或 CLI 源码。
- S1b 新增 `tests/Lilara.Core.Contract.Tests`：**不拿 `InternalsVisibleTo`**，只引用 `Lilara.Core`，从外部视角完成「开数据根 → 建会话 → 推进一步 → 重开读回」。它守的是「Bridge 不需要 internal」这条编译期契约。顺带删除了 `Lilara.Core.Tests.csproj` 里那条无效的 `InternalsVisibleTo`（授权方应在产出程序集侧）。
- 基线测试：`Lilara.Core.Tests` 1582/1582 + `Lilara.Core.Contract.Tests` 4/4 通过（2026-10-25，`dotnet test AgentLilaraCode.sln`）。
- S1a 验证：`dotnet build AgentLilaraCode.sln --no-restore` 成功（0 warning/0 error）；隔离 `LILARA_HOME` + 独立端口 18763 的 CLI 启动 smoke test 显示空数据根并成功监听，测试进程已停止。用户数据目录 `C:\Users\16640\.lilara` 未发现 heartbeat 标记，未清理或修改其内容。
- Kernel 的架构守卫要求依赖方向单向；`README.md` 明确放弃 `MasterEngine + ISubEngine + SpawnCheck + SessionPool` 这类 ALP 生态。

### 关键差异

| 主题 | ALP | ALC | 迁移约束 |
|---|---|---|---|
| 模型面历史 | `_history: List<Message>` 常驻累积 | append-only typed timeline，每步重新投影 | 不能直接共享/双写旧 history |
| 压缩 | 摘要后新会话接管 | checkpoint + suffix projection | 首版保留 ALP compact 宿主语义 |
| 工具 | 参数为位置化 `List<string>`，插件服务定位器 | 原始 JSON 参数、typed `ToolOutcome/Block` | 必须有单一、可测的 ToolBridge |
| 生命周期 | burst/wait/claim/weight/资源池 | 一步推进 + Host 调度 | 生命周期留 ALP，Stepper 只提供一步 |
| 扩展 | 23 插件项目、组件和 13 项左右服务契约 | 进程内轴插件，无 DLL 工具插件 | 不把 ALP 插件 ABI 带入 Kernel |
| 存储 | SQLite 业务库 + ALP spec/state/history | 文件式 SessionStore + BlobStore | 独立数据根，迁移必须显式、有报告 |
| 流式 | ALP 模型客户端具备流式链路 | ALC `IModelAdapter.CallAsync` 当前非流式 | express/channel 切流前先决定是否接受非流式 |

## 分阶段路线与阶段门槛

### S0 — 本 ADR 与基线冻结（完成）

完成：记录两仓 commit、工作树状态、测试结果、单写者、宿主/Core 所有权与已知基线问题。

通过条件：方向和边界经用户确认；此阶段不改运行时代码。后续阶段须分别审批，不由本 ADR 隐含授权。

### S1a — ALC 内核与 CLI 拆分（完成）

- `Lilara.Core` 改为 class library；CLI 组合根独立为 `Lilara.Cli`，仍生成 `lilara` 可执行文件。
- `Lilara.Head` 与测试直接引用 Core library；CLI 引用 Core。
- CLI 需要的内部成员通过链接编译的组合根源码访问；不通过把测试/CLI 范围的内部 API 全面改 public 来凑边界。
- 本阶段不升级 ALP，不发布 NuGet，不改变 ALC canonical API 或磁盘格式。

验证：ALC solution build 成功（0 warning/0 error）；ALC tests 1385/1385 通过；隔离数据根和端口下 CLI 启动成功，测试进程已停止。

### S1b — 固定内核版本与包化（完成）

- 新增统一迁移的前置约定：Bridge 只引用固定版本的 `Lilara.Core` package/feed，不得项目引用 CLI Exe；内核 API 必须通过稳定的宿主门面暴露，Bridge 不使用 `InternalsVisibleTo`、不链接 CLI 源码。
- 已完成：`Lilara.Core` 包身份与版本固定（`0.1.0`）；本地 feed 产出路径与命令固定（`artifacts/packages`，`dotnet pack`）；公开门面 `KernelRuntime` 覆盖建/载/推进/续跑/读时间线；`Lilara.Core.Contract.Tests` 在**无 InternalsVisibleTo** 前提下验证整条宿主路径。
- 未做（有意）：没有给 ALP 侧建 nuget.config 或加包引用——那属于 S2 的 Bridge 骨架，且需要先确定 feed 的存放位置（仓库内 `artifacts/` 只服务本机开发，跨仓消费要在 S2 决定是 PATH 环境变量、共享目录还是 CI 产物）。
- 验证：ALC 全解决方案 `dotnet build` 0 warning / 0 error；`dotnet test AgentLilaraCode.sln` = 1582 + 4 全绿；`dotnet pack` 产出 `Lilara.Core.0.1.0.nupkg`。
- CLI 行为保持不变（`Lilara.Cli` 未改）。
- 背景提示：`a128b28` 在拆分 Core/CLI 的同时删除了 24 步熔断（`MaxConsecutiveToolSteps`）。这是切流前需要单独确认的行为变化，S1b 未恢复也未沿用。

### S1c — ALP net10 宿主兼容（完成探测）

- `AgentCoreProcessor` 与 `AgentCoreProcessor.Tests` 已切到 net10.0；`LilaraConsoleClient`、`AgentLilara.PluginSDK` 和其余插件保持 net8.0。
- `Plugin.WorkingTools` 因直接引用 `AgentCoreProcessor` 同步切到 net10.0。
- `Directory.Build.props` 的 `HostPluginsDir` 已从 net8.0 路径改为 net10.0 路径；整套解决方案构建后插件正确复制到主程序运行目录。
- `TableCard.razor` 的 lambda 解构变量 `field` 改名 `fieldName`，以兼容 net10 默认 C# 14 的 `field` 关键字。
- 验证：ALP 全解方案 build 0 error；`AgentCoreProcessor.Tests` 173/173 通过；隔离 `--dev` 启动成功，HttpBridge/WebUI 与 Message/Timer/Session/Vision/Mind 引擎就绪。
- 未解决告警（发布阻断项）：SQLitePCLRaw.lib.e_sqlite3 2.1.6 NU1903 高危公告、PluginLoader CA2022 短读告警、测试 FormatterServices SYSLIB0050 过时告警。
- 并发状态提示：ALC 侧当前有另一个会话在运行，构建 `Lilara.Cli` 时输出 DLL 被 `lilara (14100)` 锁定；本会话未终止该进程或修改其工作树。

### S2 — Bridge 骨架、数据转换和影子验证（骨架完成）

- 在 ALP 外围加入 `ISessionBrain`/等价 seam，默认固定使用 Legacy。
- 建立独立 Kernel 数据根和会话绑定；Bridge 做 provider、prompt、消息、tool catalog、tool result 和 event 的显式映射。
- 影子请求使用 stub/replay，不调用真实付费模型、不执行有副作用工具、不外发消息、不写 Legacy 历史。
- 比较系统提示、时间线、工具 schema/排序、模型参数和映射结果；差异须进入允许清单。
- 通过条件：工具映射、重名/权限、文本/图片/附件、失败/取消/超时、native tool pairing 和单写者测试通过；Legacy behavior 完全不变。

**已落地（2026-10-25，骨架）**

- 包消费方式：feed 在**两个仓库之外**的 `E:\Workspace\LilaraPackages`，通过用户级 NuGet 源 `lilara-local` 注册；ALP/ALC 仓库里都不出现对方路径。
- `AgentLilara.SessionBrain`（零依赖）：`SessionBrainMode` / `ISessionBrain` / 中性 DTO（消息、部件、工具、provider、结果）。ALP 生产代码只认这套契约。
- `AgentLilara.KernelBridge`（**唯一同时触碰两边的程序集**）：`PackageReference Lilara.Core 0.1.0` + 中性契约；不跨仓项目引用、不链接源码、不用 `InternalsVisibleTo`。
- ALP 侧接缝：`AgentCoreProcessor/Engine/Session/Brain/`
  - `LegacyRequestMapper`：在 Legacy 调用成功返回后，从**客户端实际持有的历史/工具/配置**同步取快照（不是从 `_history` 猜）。
  - `SessionBrainRuntime` + `SessionBrainWiring`：默认 `Off`；`LILARA_SESSION_BRAIN=shadow` 启用，报告默认写 `<Storage>/KernelShadow/reports.jsonl`，可用 `LILARA_SESSION_BRAIN_REPORT` 覆盖。非法值或 `live` **启动即抛**，不静默退回。
- 影子实现：`KernelShadowBrain` 只做「DTO → ALC `ModelRequest` → 规范化比较」，**不建内核会话、不写数据根、不调模型、不执行工具**。报告区分 `Differences`（真实不等价）与 `Unsupported`（已知能力缺口）。
- 已知能力缺口（全部显式报告，不静默丢弃）：`enableCaching`、`forceToolCall`、`disableImageMessages`、`extraBody`、effort、system 角色的注入消息、tool_result 的 `IsError`、读不到字节的图片。
- 验证：ALP 全解决方案 build 0 error；`AgentCoreProcessor.Tests` **203/203** 通过（含 30 条新增接缝/转换/比较/配置测试）。ALC 侧未改动，仍为 1582 + 4。
- 尚未做（S3 起）：协议适配器级线上字节金样、工具表到 ALC `ITool` 的桥接与权限投影、内核会话/数据根绑定、原生 tool pairing 的完整往返。

### S3 — Review，再 Task 子会话

- 优先迁移无频道占用的 review；再迁移 task/子 agent。
- 资源阻止退出、父子关系、邮箱、wait/report/stop、队列认领仍由 ALP Host 管理。
- 对 Kernel session 的失败恢复和显式导出/冻结路径做演练。
- 通过条件：端到端剧本、工具副作用 exactly-once、进程强杀恢复、回滚交接均通过。

### S4 — Express canary

- 先确认非流式是否可接受；若不可接受，先单独设计 Protocol 层 streaming 扩展，不在 Kernel 里掺 UI/频道事件。
- 单测试频道 canary；weight、私聊/@、冷启动恢复、空闲回收和 `escalate` claim 移交仍由 ALP。
- 通过条件：无重复外发/孤儿会话/卡死；延迟、工具错误和历史增长达到双方预先定的基线门槛。

### S5 — Channel/working 最后切流

- 先保持频道/参与者/图片引用/组件通知/Mind 的现有语义，逐项将纯状态信息移到 ALC floating slot，避免一批重写上下文行为。
- compact 接管仍由 ALP 驱动；暂不同时启用 ALC checkpoint/reclaim。
- 通过单频道 canary 后按确定性 allowlist 放量；每波观察后再继续。
- 通过条件：私聊、@、图片/OCR、转发、Mind 工具、组件、定时唤醒、WebUI trace/history 和 compact 接续验证通过。

### S6 — 存量迁移与 Legacy 退役

- 存量会话默认不强制迁移；优先在新建、compact/escalate 等自然边界切换。
- 仅对明确要求连续性的会话提供一次性离线导入器 + dry-run 报告；旧数据只读保留。
- ALC 专属事件无法安全导出时必须冻结/拒绝回滚并解释，禁止静默丢弃。
- 只有某类型无 Legacy 活跃会话且回滚窗口结束，才考虑逐类型移除 Legacy brain 分支。

## 发布、开关和回滚规则

- 配置按 `off | shadow | canary | live` 分层；默认 `off`。新会话固定 engine/version，禁止存量会话热切换。
- 每会话模型面历史恰好一个写者；所有数据路径可审计地区分 Legacy/Kernel。
- Canary 的上线/回滚需有观察周期和阈值；出现重复外发、两边同时写、工具调用悬空或数据格式无法读取即停止放量。
- 回滚时先停止 Kernel 新步，再显式导出或冻结 Kernel 时间线，确认单写者后才恢复 Legacy；不删 Kernel 数据、不直接互换两边 state 文件。

## 验收矩阵

1. **基线**：每次 ALC 全量测试需通过；当前唯一失败须先归档/修复并有单独记录。ALP 173 项测试维持全绿。
2. **架构**：Core 不引用 ALP；只有 Bridge 触及两侧模型；架构测试/项目依赖审计覆盖该边界。
3. **请求等价**：固定剧本比较模型、provider、协议、提示词槽位、时间线事件、工具 schema/排序及参数。
4. **副作用**：工具成功/失败/超时/取消、图片附件、connect/wait/end/compact/escalate 每项只执行一次且语义符合 ALP。
5. **持久化**：独立根、加载、撕裂/损坏恢复、单写者、导入报告、导出/冻结交接有测试。
6. **端到端**：隔离 Storage + FileAdapter/HttpBridge + stub 模型验证，不中断正式实例、不连真实 QQ；真实 QQ 仍由用户执行最终实测。
7. **运行观察**：延迟、模型错误率、工具错误率、重复输出、token/上下文增长和恢复成功率在每次扩大 canary 前审阅。

## 下一阶段实施前门槛

1. 明确 S1 采用 ALC 内核库化方案与版本发布位置。
2. 评估 ALP net8 进程是否必须升级到 net10；必须先做无业务行为改变的兼容验证。
3. 决定 express/channel 对非流式的容忍度。
4. 明确 ALC Provider 能否表达 ALP `extraBody`、强制工具、禁图等设置；不支持的配置必须 fail closed。
5. 明确存量会话是否需要导入及哪些数据可接受有损映射。

## 参考入口

- ALP：`AgentCoreProcessor/Engine/Session/RuntimeSession.cs`、`SessionAgentHost.cs`、`SessionPersistence.cs`、`Tool/ToolExecutor.cs`、`Tool/ToolRegistry.cs`、`Engine/Session/SessionTypeProfiles.cs`、`AgentLilara.PluginSDK/ITool.cs`、`Models/Message.cs`。
- ALC：`src/Lilara.Core/Session/Stepper.cs`、`SessionHandle.cs`、`Hosting/CoreHost.cs`、`Kernel/Block.cs`、`Kernel/TimelineEvent.cs`、`Session/SystemBlock.cs`、`Session/TimelineRender.cs`、`Storage/SessionStore.cs`、`Storage/BlobStore.cs`、`tests/Lilara.Core.Tests/ArchitectureTests.cs`。
