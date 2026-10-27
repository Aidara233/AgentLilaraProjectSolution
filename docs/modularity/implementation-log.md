# 实施日志

> 记录已经落地的改动、每笔的验收结果，以及**刻意没做的事**（连同理由）。
> 设计文档见同目录其余各篇；本文件只讲"实际做了什么、验证了什么"。

---

## 已完成

| 提交 | 内容 | 编译 | 测试 | 行为变化 |
|---|---|---|---|---|
| `46eb982` | **A1** `Engine/Axes` 轴表模块（7 条轴 + 存储 + 端口），state 格式 2→3 | ✅ 0 error | 385 pass | 无（无消费者） |
| `7f1d0a1` | 九份设计文档 + `modules.json` | — | — | — |
| `850de3b` | 记录 A1 实施状态与 `InContext` 修正 | — | — | — |
| `becc088` | **A2** `Engine/Prompts` 三槽位契约 + 顺序契约 | ✅ | 404 pass（+19） | 无（无消费者） |
| `df302d1` | **A3-1** `SessionAgentHost` → `Engine/Context/SessionContextHost`（纯搬移） | ✅ | 404 pass | 无 |
| `e4c5971` | **A3-2** `ContextAssembler`：轴 → 浮动槽投影 + 装配器 | ✅ | 419 pass（+15） | 无（未接线） |
| `b63f289` | **A3-3** `GlobalPromptSections` 抽出 + 工具表锁定改为显式 mutation（H1） | ✅ | 419 pass | 无（等价重构） |
| `5c3f4e7` | **A4-1** `ToolEffectInterpreter`：工具结果解释改纯函数 | ✅ | 441 pass（+22） | 无（等价重构） |
| `48bbafc` | **A4-2** `StepPhase` 粗阶段字段（只读诊断） | ✅ | 441 pass | 无（只读字段） |
| `1b9305c` | **A4-3** `AgentInvokeRequest`：每轮参数改传参（H2） | ✅ | 441 pass | 无（等价重构） |
| `c54c2bf` | **B-1** `RuntimeSession` 拆 partial：inbox / output / activation | ✅ | 441 pass | 无（纯文件拆分） |
| `9f00978` | **B-5** 拆 partial：lifecycle / children | ✅ | 441 pass | 无 |
| `ecea9bf` | **B-3a** 拆 partial：core-switch | ✅ | 441 pass | 无 |
| `3fd8471` | **B-3b（重做）** 拆 partial：components（一刀一 build） | ✅ | 441 pass | 无 |
| `28eb4b1` | **测试** `AxesCatalog` + `AxisStore` 契约（40 条） | ✅ | 481 pass | 无（只加测试） |
| `b2f64c9` | **B-6** `SessionEngine` 拆 6 个 partial（1360→406） | ✅ | 481 pass | 无 |
| `b02fb86` | **B-3b'** 拆 partial：bootstrap（1212→961） | ✅ | 481 pass | 无 |
| `2b484c9` | **B-3b''** 组件启停三件并入 Components.cs | ✅ | 481 pass | 无 |
| `ce6f78a` | **B-3c** 拆 partial：频道连接（939→856） | ✅ | 481 pass | 无 |
| `863c76d` | **端到端冒烟**：隔离 Storage 真实跑一轮（见下） | ✅ | — | 已验证 |
| `3a662f1` | **B-2 第 1 步** 拆 partial：持久化（856→767） | ✅ | 481 pass | 无 |

测试从 385 → **441**，净增 56 条，全部是纯内存单测（不建会话、不碰存储、不调模型）。

---

## 实施中改变的判断（三处，都已回写文档）

| # | 原计划 | 实际 | 理由 |
|---|---|---|---|
| 1 | `permission` / `network` 进上下文 | **`InContext = false`** | 未接线的轴若进提示词，模型读到"权限 off"会自己拒工具——**假机制**比没有更糟 |
| 2 | 类名 `Axes` | **`AxesCatalog`** | 类型名与命名空间 `Engine.Axes` 同名，兄弟命名空间里 `Axes.Xxx` 被解析成命名空间，编译 CS0234 |
| 3 | `IAxisPort.FreezeNow()` | **只用 `Freeze(values, now)`** | 接口保持最小：消费者拿到的都是外面传进来的冻结快照，不需要自己 Freeze |

---

## 刻意没做的事（连理由）

| 项 | 为什么不做 | 什么时候做 |
|---|---|---|
| **H2：step 不再直写 `_agentCore.EngineType` / `AdditionalTools`** | **已完成**（`1b9305c`）：改为 `AgentInvokeRequest` 传参。`request == null` 时回落读字段，兼容 `Agent.ChatAsync` 与切核路径 | — |
| **把 `RunAsync` 420 行拆成方法** | 细分 phase 必须与方法边界一起定，否则出现"12 个 phase 值、3 个赋值点"的假精度 | B 阶段按 20 部件切文件时 |
| **结果回收（Reclaim）** | 已确认不做 | 不做，但后果写在 `ContextAssembler` 类型注释里：结果只增不减，压缩压力全落到 L3 |
| **`ILoopBus` 删除（N1）** | 它在 `Engine/Worker/`，属于 C/D 阶段的文件地盘，现在删会与后续搬移冲突 | C 阶段 |
| **`ClassId` 注册表（U9/N2）** | 属 graph 模块内部，与 A 阶段无关 | 计划"后续"分支 |

---

## 验收口径（每笔都跑了）

1. `dotnet build AgentCoreProcessor.csproj` → **0 error**；3 个警告全是既有的（NU1903 ×2 包漏洞、CA2022 一处 FileStream 读）。
2. `dotnet test AgentCoreProcessor.Tests` → **全绿**（441）。
3. 新增测试必须是**纯内存**：不建会话、不读写数据根、不联网。这是 A 阶段"零行为变化"唯一能自证的方式。

**没有做的验证**：没有跑 `--test` 端到端模式（需要真实配置与网络），也没有做线上请求体的逐字节对比（V2 那条验收）。
所以"零行为变化"目前建立在**代码等价性 + 单测**上，不是建立在端到端对拍上——这一点必须如实记着。

---

## 端到端冒烟验证（已做，2026-10-27）

用 `--test` 跑了一次真实进程，**在 `bin` 目录下的隔离 Storage 里**（不是项目的 `../Storage`；真 Storage 先备份了 972MB 以防万一）。

投一条消息后：**会话被真实创建并跑完一轮，全程无异常**。

| 观察点 | 证据 | 覆盖的重构 |
|---|---|---|
| 5 个引擎自启 | `Message/Timer/Session/Vision/Mind` 全 `IsAlive=True` | `SessionEngine` 六个 partial 的构造与装配 |
| 会话被创建 | `Sessions/8296a6626b3f/spec.json`，`type=express` + 两个 inputPorts | `SessionEngine.Assembly.cs`（`CreateSession` 555 行那块） |
| 频道占用建立 | `connectedChannelId:"1"`、`connectedMode:"Full"` | `SessionEngine.ChannelClaim.cs` + `RuntimeSession.ChannelConnect.cs` |
| 跑完一轮 | `roundCount: 1` | `RunAsync`（尚未拆分，原本整块） |
| 组件通知注入 | history 第 1 条 `[组件通知] 当前余额: 0.0 币…` | `GlobalPromptSections`（A3-3 抽出）+ `RuntimeSession.Bootstrap.cs` |
| 每轮上下文 | history 第 2 条 `[系统] 当前时间…` | `SessionContextHost.BuildRoundAsync`（A3-1 搬移） |
| 消息排空与格式化 | history 第 3 条 `[新消息] ⚠️ [!! @提及 !!] …` | `RuntimeSession.Inbox.cs`（B-1） |
| 轴值落盘 | `state.json` 的 `axes` 含全部 7 条，`temperature:"0.7"`、`initiative:"persistent"` | A1 的 `AxisStore` + `SessionPersistence` |
| 快照格式升级 | `state.json` 的 `"formatVersion": 3` | A1 的 2→3 升版 |
| 干净收尾 | `status:"Completed"`、`compressionTier:"L3"` | `RuntimeSession.Lifecycle.cs` + `ToolEffectInterpreter` |

**证明了**：28 笔改动合起来后，程序能启动、能建会话、能装配上下文、能跑完一轮、能把新字段落盘、能干净收尾。

**没证明**：
- 无真实模型调用（隔离 Storage 无可用凭据）——**「请求体字节与改动前一致」仍未验证**；
- 只覆盖 `express` 一条路径，未覆盖 `channel` / `task` / 子 agent / 升级；
- `--test` 退出码 1 是 20s 超时所致，不是崩溃。

**副作用**：`bin/Debug/net10.0/win-x64/` 下新生成了一个约 2GB 的 `Storage`（构建产物目录，可删）；项目 `../Storage` 未改动。

## 如何重跑这次冒烟验证（2 分钟）

不必碰真实数据。`--test` 会在 exe 目录下自建 `paths.json` 与 `Storage`，所以按下面做就是隔离的：

```sh
cd AgentLilaraProjectSolution/AgentCoreProcessor/bin/Debug/net10.0/win-x64
printf '这是一条冒烟测试消息\n' > Storage/FileAdapter/input/001_smoke.txt
timeout 120 ./AgentCoreProcessor.exe --test --timeout 20
# 看结果：
ls Storage/Sessions/                 # 应出现一个新会话目录
cat Storage/Sessions/*/state.json     # 应看到 formatVersion:3 + axes 七条 + status
cat Storage/Sessions/*/history.jsonl  # 应看到 [组件通知] / [系统] 当前时间 / [新消息]
```

**判据**：会话目录被创建、`state.json` 里 `formatVersion` 为 3 且 `axes` 完整、`history.jsonl` 至少三条（组件通知 / 时间 / 新消息）、`status` 为 `Completed`、进程无异常堆栈。

**注意**：
- 退出码 1 = 超时（无回复），不是崩溃；
- 隔离 Storage 里没有可用 provider 凭据，所以它**不会**验证模型请求体；
- 跑完可以在 `bin/Debug/net10.0/win-x64/` 下删掉那个 `Storage`（约 2GB）。

## 当前 B 阶段收尾状态

| 切片 | 状态 | 主文件行数 |
|---|---|---|
| B-1 inbox / output / activation | ✅ | 1646 → 1469 |
| B-5 lifecycle / children | ✅ | 1469 → 1398 |
| B-3a core-switch | ✅ | 1398 → 1276 |
| B-3b components | ✅ | 1276 → 1212 |
| **B-6 SessionEngine 六切** | ✅ | 1360 → 406（另一个文件） |
| B-3b' bootstrap | ✅ | 1212 → 961 |
| B-3b'' 组件启停三件 | ✅ | 961 → 939 |
| B-3c 频道连接 | ✅ | 939 → 856 |
| **B-2 第 1 步 持久化（搬文件）** | ✅ | 856 → 767 |
| **B-2 第 2 步 `ISnapshotPort` 注册表** | ⬜ **未做**（设计改动） | — |
| **B-4 `RunAsync` 426 行拆方法** | ⬜ **未做**（风险最高） | — |

`RuntimeSession.cs`：**1646 → 767 行**，9 个部件文件共 1224 行。

## 下一步

| 顺序 | 内容 | 状态 |
|---|---|---|
| 1 | **B**：session 拆 20 部件（切文件，不切 csproj） | 进行中（7 个部件已落地） |

### B 阶段进度

| 批次 | 切什么 | 状态 |
|---|---|---|
| B-1 | `inbox` + `output` + `activation` | ✅ `c54c2bf` |
| B-5 | `lifecycle` + `children` | ✅ `9f00978` |
| B-3a | `core-switch` | ✅ `ecea9bf` |
| B-3b | `components` | ✅ `3fd8471`（重做，见下） |
| B-3b' | `bootstrap`（含 `ApplyToolEffectAsync` 接线） | 待做 |
| B-3b'' | `Enable/Disable/ToggleComponentAsync` 三件 | 待做 |
| B-4 | `step` 拆方法（12 阶段） | 待做 |
| B-2 | `snapshot` + `ISnapshotPort` | 待做（**必须最后**） |
| B-6 | `SessionEngine` 的 8 个 Singleton | 待做 |

当前 `RuntimeSession` 家族：主文件 **1212 行** + 7 个部件文件共 674 行。

B 的前置条件已经具备：`step` 的 12 阶段表、`ToolEffectInterpreter` 纯函数、`ISnapshotPort` 的设计（D3 定为扁平 payload）、H1/H2 两处反向数据流已消除。

### B 阶段的做法建议（未执行）

**不要一次切 20 个文件**。按"每一刀都能独立编译 + 测试全绿"切：

| 批次 | 切什么 | 为什么这个顺序 | 状态 |
|---|---|---|---|
| B-1 | `inbox` + `output` + `activation` | 三者最独立，与 `_tools`/`_agentCore` 无关，先建立节奏 | ✅ `c54c2bf` |
| B-2 | `snapshot` + `ISnapshotPort` | 它要遍历各部件，必须**最后**一个切（否则每次加部件都要改它） | 待做 |
| B-3 | `core-switch` + `components` + `bootstrap` | 三者围绕 `_agentCore` / `_componentHost` / `_initActions`，一起动 | 待做 |
| B-4 | `step` 拆成方法（12 阶段 → 方法边界） | phase 字段已在位（`StepPhase`），拆完对照 phase 赋值点即可验证 | 待做 |
| B-5 | `children` + `resources` + `lifecycle` | 收尾，都是小件 | 待做 |
| B-6 | Singleton 8 个（pool/assembly/type-profiles/escalate/subagent/brain-bridge） | 在 `SessionEngine` 里，与上一组隔离 | 待做 |

⚠️ **B-2 必须最后**：`ISnapshotPort` 是对"所有有状态部件"的遍历，先切会让它反复返工。

### B-1 实测结果

`RuntimeSession.cs` **1646 → 1469 行**，三个新文件共 278 行：

| 文件 | 行数 | 内容 |
|---|---:|---|
| `RuntimeSession.Inbox.cs` | 111 | `AdvanceChannelCursor` + `DrainInputPortsRaw` |
| `RuntimeSession.Output.cs` | 53 | `SendOutput` |
| `RuntimeSession.Activation.cs` | 114 | `WaitForActivationAsync` + `WaitForActivationCoreAsync` + `ResetAllMpQueueCounts` |

做法：`RuntimeSession` 改为 `partial`，按方法边界整段搬迁，**不改一行方法体**。
编译踩到一处：`Inbox` 里的 `JsonConvert` 需要单独 `using Newtonsoft.Json`（partial 文件的 using 不共享）——这类漏 using 是纯文件拆分的唯一真实风险，编译期就能抓住。

### B-3b 失败并回滚（记录在案）

`components` + `bootstrap` 两个块**同一轮里连着 cut 了两次**，结果：

1. 第一刀（`731..748`）实际切进了 `ToggleComponentAsync` 中间——因为此前几次切割已让行号偏移，我用的还是旧偏移量；
2. 第二刀（`485..702`）落下两行注释头（`/// <summary>` 与首行说明），主文件里出现孤立注释块；
3. 编译报 `CS1001` / `CS1519`。

**已 `git checkout` 回滚到 `ecea9bf`，删除未完成的 partial 文件，build + 441 测试重新确认全绿。** 工作区干净。

**教训（已写进 B-3a 的 commit，这里再记一次）**：
- 行号搬运的偏移量**每次切割后都会变**，不能复用上一轮记下的行号；
- **一次只切一刀，切完立刻 build**，不要连切两组再统一编译；
- 更有抗性的做法：**按方法签名定位**（先 `grep -n` 重新取当前行号），而不是沿用旧数字。

---

## B-4 主循环拆分进度（RunAsync 342 → 241 行）

| 批次 | 抽出的方法 | 状态 | RunAsync |
|---|---|---|---|
| — | （起点） | — | 342 |
| B-4a | `InjectContinueReminderAsync` | ✅ `32e7521` | 327 |
| B-4b | `InterpretToolEffectsAsync` + `DecideStepAction` + `StepAction`/`ToolBatchOutcome` | ✅ `9b0c725` | 302 |
| B-4c | `ExecuteToolsAsync` | ✅ `2384f45` | 285 |
| B-4d | `InvokeModelAsync` | ✅ `8188e1b` | 268 |
| B-4g | `DrainMidRoundAsync` | ✅ `0441d2d` | **241** |
| **B-4e** | `PrepareInferenceAsync`（推理前准备 + 压缩锁定 mutation） | ⬜ 未做 | — |
| **B-4f** | 入口三段（`AwaitActivation` / 组件通知 / `CollectInput`） | ⬜ **故意留最后** | — |

### 两个决策记录

**1. B-4d 的风险被设计消掉了，不是被绕过去**
映射表里它被标成最高风险，因为 `roundSpan` 的 `using` 覆盖「模型调用 + 工具执行」两段。
实际做法是**把 span 留在主循环、作为参数传进 `ExecuteToolsAsync`**——
`using` 的生命周期一个字节没变，风险归零。风险来自"搬 span"，不搬就没有。

**2. B-4f 故意不做**
它要动 `awaitingInput` 与 `StepAction` 的交互，而那是 express 会话"被动等待"的根。
这类错误**编译能过、481 个单测全绿、只有冒烟能发现**——
而当前环境的冒烟**覆盖不到** express 被动等待（没有有效模型凭据，不会有真实回复）。
所以 B-4f 应留给能跑真实模型调用的环境。

### 这一轮被编译器抓到三次

| 次 | 症状 | 教训 |
|---|---|---|
| B-4c | 抽出方法后 `output.ToolCalls` 变得可能为空（CS8602/CS8604） | 不用 `!` 糊：把 `calls` 作为显式参数，让非空保证跟着参数走 |
| B-4d | `ModelOutput?` 不能隐式转 `ModelOutput`，`!` 也无效 | `ModelOutput` 是 `readonly struct` ⇒ `Nullable<T>`，要 `.Value` |
| B-4g | 少一个收尾大括号（CS1513） | 行号搬运时括号计数靠编译器兜底，切完立刻 build |

### N3 与 B-2a：模型图工具及快照采集

- `70b1b65`：内建节点类型目录，端口由探针节点读取；参数示例与目录覆盖校验。
- `ea6170f`：注册 wire_describe / wire_validate / wire_apply / wire_mount；生产加载器校验、原子替换、.history 前后源码审计。接口与限制见 `wire-tools.md`。
- B-2a：BuildStateSnapshot 改为七个 ISnapshotPort 采集入口；强类型字段选择器检查重复与遗漏。原有 15 个属性全部覆盖，JSON 格式仍为 v3（仅内部组织改变，无需升版）。资源恢复顺序未改，完整 restore 注册仍待拆分。
- 主程序测试 490 项通过，GraphRuntime 33 项通过；新增快照归属冲突、遗漏与集合隔离检查。未执行真实模型/适配器冒烟。既有包漏洞和弃用 API 警告仍存在。

### B-2b：纯状态恢复入口

ISnapshotPort 同时提供采集与恢复，RuntimeSession.RestoreFromSnapshot 经同一注册表分发。生命周期、轮次、父关系、连接内存状态与轴值由部件恢复；历史另行灌入。ConfigureCores、频道端口/claim/游标、子关系与压缩初始化仍由 RestoreAll 按既有依赖顺序负责，未改变原有语义。新增恢复→快照对照验证，主程序 491 项测试通过。格式仍为 v3；未进行真实模型冒烟。

### C 前置：宿主框架配置同源

Directory.Build.props 增加 HostTargetFramework，主程序 TargetFramework 与 HostPluginsDir 同源。MSBuild 属性检查：主程序仍 net10.0、Plugin.BasicTools 仍 net8.0，插件目标目录均为宿主 net10.0/win-x64/Plugins。主程序构建通过（既有 NU1903/CA2022 警告）。

### C 前置：GraphSandbox 归入测试夹具

受版本控制的 7 个沙箱文件迁入 tests/fixtures/AgentLilara.GraphSandbox；命名空间与方法体保持原样。修正两个项目引用、测试项目引用及解决方案路径/分组（归 tests）。未移动 bin/obj；新目录以本地缓存离线还原后 GraphSandbox.Tests 17 项通过。

### C1：首个业务程序集 Lilara.Logging

日志核心九文件及 LogAccessImpl SDK 桥迁入 src/Lilara.Logging，十文件均为 100% 原文迁移、命名空间保持。新库依赖 PluginSDK、Microsoft.Data.Sqlite 8.0.0、Newtonsoft.Json 13.0.4，宿主单向引用新库。LogCleanupService/LogCleanupConfig/SignalConfig 保留宿主，避免配置/业务数据库回指。离线缓存还原、主程序编译、491 项主程序测试通过。未跑真实适配器或模型；SignalContext 静态状态尚存，本次仅确立编译边界，不宣称 R5 或数据流日志改造完成。

### C1：Lilara.Data

Database 全部 32 文件迁入 src/Lilara.Data，保持命名空间且均为 100% 原文迁移。库引用 sqlite-net-pcl 1.9.172、Microsoft.Data.Sqlite 8.0.0、Newtonsoft.Json 13.0.4；宿主单向引用。对宿主与测试声明 InternalsVisibleTo，保留仓储 internal 访问范围，避免本次扩大公开 API。离线缓存还原、主程序编译与 491 项主程序测试通过。仓储接口、进程静态状态与最终装配规则仍需后续改造；未启动真实适配器/模型。

### C1：Lilara.Context 首切

Prompts 两文件与 Axes 五文件迁入 src/Lilara.Context，均保持源码原文、命名空间和 internal；通过友元宿主/测试维持访问。新库只依赖 BCL。ContextAssembler、SessionContextHost 与频道上下文仍留宿主，因此本次是 Context 部分提取。离线缓存还原、主程序构建、491 项主程序测试通过；未跑真实模型冒烟。

### C1：Lilara.Llm 模型客户端首切

迁移 Models 三文件与 Client 七文件至 src/Lilara.Llm。客户端直接引用 PluginSDK，脱离宿主 global using；Claude 请求转储目录改由构造注入，Processor 传入原 Storage/Logs/ClaudeRequests 路径。新库依赖 PluginSDK、Lilara.Logging、Anthropic.SDK 5.10.0、OpenAI 2.10.0、Newtonsoft.Json 13.0.4，友元宿主/测试。Core/Processor 与 OCR/embedding/vision provider 暂留宿主。主程序编译和 492 项测试通过；新增离线失败请求转储/脱敏验证。离线还原两个间接依赖缺缓存 10.0.3，NuGet 以 10.0.5 解析并报 NU1603；未联网且未验证真实模型请求。未宣称契约总线与静态状态治理完成。

### C1：Vision/OCR 与 Embedding providers

OCR/Vision 六文件迁入 src/Lilara.Vision，Embedding 五文件及纯 BCL VectorUtil 迁入 Lilara.Llm。SiliconFlowVisionProvider 不再接受宿主 PhaseConfig，由 VisionEngine 三处展开原配置的 endpoint/model/temperature/maxTokens/promptTemplate。新 Vision 库引用 Logging/Newtonsoft，Llm 增加 ONNX 既有版本；保持命名空间与友元过渡。主程序构建、492 项测试及 diff --check 通过。未运行真实 OCR、ONNX 模型或外部模型请求；两项缓存依赖替代 NU1603 与既有 API 警告仍在。VisionEngine 业务编排与 PhaseConfig 仍留宿主。

### C5：WorkingTools 脱离宿主引用

IInjectProvider/InjectContext 纯契约原文迁入 PluginSDK，保留 AgentCoreProcessor.Engine 命名空间。宿主添加真正的 TypeForwardedTo 兼容旧程序集名字；删除 WorkingTools 对宿主的 ProjectReference，TFM 仍 net10.0。插件独立构建零警告，主程序 493 项测试通过；新增 SDK 类型身份/宿主转发解析检查。未用旧插件 DLL 做隔离 ALC 运行冒烟。

### 图数据流日志首切

EvalContext 新增 PortTransfers（实际消费连线的三态、载荷类型、顺序，不存正文）与 EvaluatingNode（Evaluate 异常定位）。Solver 在输入读取点记录；运行时仍不引用日志实现。KeywordGraphRunner 把每拍 class/instance/trace/seq、成功/异常类型、未求解节点、求值顺序与端口流写为 graph.flow.beat 结构化 Signal 事件。新增 Known/NoEvent/Unknown、依赖环与抛错节点定位测试。GraphRuntime 38 项与主程序 493 项测试通过。尚未验证真实运行日志入库/UI 展示；其他图入口及旧生命周期/手工信号尚未迁移，全面信号系统改造仍未完成。

### C1：Lilara.Graph 管理层

图管理/挂载/订阅/运行器等九文件迁入 src/Lilara.Graph，依赖 GraphRuntime 与 Logging，友元宿主/测试。KeywordGraphRunner 中两个宿主消息映射原文迁至宿主 GraphEventMapper；生产调用与测试已更新。MessageGraphIntentDispatcher 发送桥仍留宿主。主程序编译、493 项主程序与 38 项 GraphRuntime 测试通过。只读复核另外发现 wire 输入保留名大小写、缺字段挂载表、频道键规范化、替换激活源兼容性及 session scope 未消费等问题，下一刀优先修复，不能因本次测试通过视为工具完整验收。

### 图工具复核修复

修复保留文件名大小写别名、缺字段挂载表误清空、频道键非规范文本、更新源码破坏现有触发源兼容性；工具暂拒尚未消费的 session scope，明确订阅需删除重建/重启。运行端也规范化频道键并拒绝不匹配激活源。新增存活 manager 重载/卸载、无效输入保留原文件及手工挂载表回归；旧兜底优先级测试改用真正 on.error 静默图。主程序 501 项测试与 diff --check 通过，未跑真实模型或适配器冒烟。

### Session 图消息消费首切

入口与轮中排空后消费 session on.message 图，通过动态挂载提供器支持运行中新增/卸载。批内相同稳定消息标识合并 normal/mention，保留 mention。active 仅执行指向当前 sessionId 的 sink.inject，shadow 只求解；不重投原消息、不改变激活判定。图变量与本会话注入为当前契约，其他动作在工具/运行端挂载校验拒绝。提供器异常记录后返回；实例仍随会话释放。新增真实图→RuntimeSession 历史注入、shadow、跨目标拒绝、卸载与故障隔离测试；旧实例隔离夹具改为专用 session 计数图。主程序 506 项测试通过，未跑真实模型轮次冒烟。session scope 工具入口据此开放，仍未支持 session 图发消息/跨会话路由/错误触发。

### 同类会话共享图定义

新增 session-type 挂载索引，scopeKey 为会话类型且规范化小写。RunnersForSession 合并类型与实例专属挂载，按 order/原表声明顺序稳定排序；类型模板按会话身份创建独立实例与变量，释放只影响该会话。宿主动态提供器传入 Type。会话图只读挂载参数自动提供 sessionId，wire 和运行端执行相同契约校验。新增类型/专属图并存、大小写匹配、跨类型不匹配、实际双图注入、变量隔离、释放回归，主程序 507 项测试通过。尚未验证真实模型轮次冒烟；适配器/频道等其他模块类型通用挂载仍待后续。

### D1 前置：激活影子范围与批次重置

GraphActivationShadow 显式记录 Comparable/Matches/Reason，仅默认普通消息权重 5 与 mention 消息可比；系统输入、timeout、wake、取消和非默认普通权重标为不可比较。消息激活结束及入口/轮中排空后按实际队列计数同步影子，修复 _seen 保留上一批深度及超阈值批次残留导致的多周期误报。新增连续等深度批次、超阈值批次、19 条边界及不可比原因测试；主程序 513 项通过。生产激活仍用旧判定；未完成 D1 全路径对拍、Inline/D5 时序证伪或真实模型冒烟，不据此启动 D2。

### 数据流日志入库集成与退出重复写修复

使用临时 LogDatabase/LogWriter/SignalContext 跑真实 KeywordGraphRunner，LogQuery 查询成功/异常拍，验证 class/instance/trace/seq、父 span、Known 端口流、失败节点与求值顺序，确认不记录载荷正文。测试恢复静态 writer/minLevel，并清理对应 SQLite 连接池。集成验证发现 LogWriter 退出时可能重写上一批已提交事件，现于尾部排空前清空批次；新增等待提交后退出的确定性回归，验证事件仅一条。主程序 516 项测试通过、diff --check 通过。未验证 WebUI 展示或真实适配器/模型；完整旧信号迁移仍待后续。

### 发布基础会话图

新增 preset.session-mention.json，on.message→event.mention→gate.if→sink.inject，以 read.mount(sessionId) 指向当前会话；复用现有 Graphs JSON 构建/发布复制规则。新增真实默认目录回退加载及 active/shadow、普通消息/提及、同类实例与跨类型隔离测试。主程序 519 项测试、diff --check 通过；未进行真实模型轮次冒烟。

### C1：Lilara.Recurring 信源首切

Engine/Source 三文件迁入 src/Lilara.Recurring，保留命名空间与源码原文，友元宿主/测试。仅依赖 PluginSDK、GraphRuntime、Logging；TimerEngine 编排仍留宿主，因此是 Recurring 部分提取。离线缓存还原、主程序编译、519 项测试及 diff --check 通过。未启动真实信源、模型或适配器。

### Recurring：心跳调度服务

TimerEngine 心跳循环提取到 Lilara.Recurring/HeartbeatService，通过委托发布 tick、查询 Session 存活及发送告警，注入 now/delay 支持虚拟时间验证。保留 240 秒默认、timer-interval 下一次延迟生效、离线严格超过一小时单次告警、恢复重置及取消/停止行为；生命周期日志和告警投递留宿主。主程序编译、523 项测试、diff --check 通过；新增四项虚拟时间回归。未进行真实周期等待或适配器告警发送冒烟。既有 NU1603/CA2022/SYSLIB0050 验证限制仍在。

### C1：Lilara.Session 资源池与规则首切

SessionPool、SessionResourceRegistry、ToolEffectInterpreter、ConnectedChannelState 与 StepPhase/StepAction/ToolBatchOutcome 提取至 src/Lilara.Session，依赖 SDK 与 Logging，保留命名空间/internal，通过友元宿主/测试过渡。StepPhase.cs 中依赖宿主 AgentCore 的 RoundContext 独立留宿主，避免库回指。其余四文件原文迁移，状态/规则未变。离线还原、主程序构建与 523 项主程序测试通过；SessionEngine/RuntimeSession、资源重建编排和占用归属仍留宿主，尚未完成 Session 整体提取或频道占用唯一所有者改造。未做真实模型/适配器冒烟。

### C1/D6 前置：频道显式配置存储

ChannelConfig 与纯 ChannelContextState 原文迁入 src/Lilara.Channel。新增 IChannelStore/FileChannelStore，显式传入 Storage 路径，保留 JSON 优先、无文件时 DB affinity fallback、JSON null 返回默认配置的既有语义；写入采用临时文件原子替换。宿主 ChannelStateManager 仅按规范化路径复用存储实例并委托，不再直接文件 IO/静态锁。离线还原、宿主构建、527 项主程序测试与 diff --check 通过；新增配置往返、回退、路径/频道隔离和 null JSON 回归。仍保留宿主静态兼容注册表，尚未完成亲和度唯一权威、频道占用归属或全部调用注入；未做真实模型/适配器冒烟。

### 频道领域：参与者派生

ParticipantView 与消息发送者/用户/人物快照及 IParticipantSource/ParticipantService 进入 Lilara.Channel，不引用数据库实体或模型协议。宿主 ParticipantSourceAdapter 对旧 IChannelDataSource 查询结果做字段映射，ChannelContextService 委托领域服务。保留数据源顺序、发送者去重、bot/无效用户过滤、姓名优先级与 Unknown=0 默认。新增逐字渲染与缺失人物/用户边界回归，主程序构建、528 项测试、diff --check 通过。整个 IChannelDataSource 及媒体/历史格式化仍待解耦，未跑真实模型/适配器冒烟。

### C 阶段整解决方案验证

完整 sln 离线缓存还原、构建与全部测试真实退出码均为 0。构建 0 错误/4 警告；主程序 528、GraphRuntime 38、GraphSandbox 17，共 583 项通过。详情及未验证范围见 verification-report.md 的对应节。未联网、未运行真实模型/适配器；包审计信息不能因离线警告减少认定已修复。

### 频道上下文完整文本基线

在替换频道数据契约前，固定旧实现无媒体路径的完整输出：增量首次日期/跨日/无时间、session 忽略游标与 express 筛选、历史尾换行、引用 XML 转义（单引号保持）、缺失引用 included 状态及 null 结果。断言同时检查消息 ContentParts 与游标/日期状态。新增四项测试，主程序 532 项通过；媒体、OCR 就绪顺序和全数据源迁移仍未完成。

### 频道媒体查询接缝与顺序基线

ChannelContextService 注入 IChannelMediaSource，默认宿主实现逐次代理 ImageStorage 路径/描述/OCR 与 VisionEngineConfig 阈值；查询时机、顺序与阈值读取条件保持。CollectImagePaths 仅有内部调用，改实例方法。新增交错内容块、重复 hash、缺路径/无效标记、OCR 有效性及阈值读取、未就绪重试、全部描述先于全部 OCR 与集合去重回归。宿主构建、540 项主程序测试通过。尚未整体迁移领域渲染契约，未调用真实媒体存储、OCR 或模型；现有警告仍在。

### 频道渲染领域迁移

渲染主体进入 Lilara.Channel/ChannelRenderer，输入 ChannelMessageSnapshot，输出 ChannelRenderedMessage/Part；查询与媒体均通过频道契约，时间转换由宿主注入。宿主 ChannelContextService 改为兼容门面，ChannelRenderSourceAdapter/Mapping 原样投影实体、时间/CSV 与模型消息，保留现有消费者。渲染语句、媒体查询顺序、null/空内容块语义由既有完整基线验证。新增三种 DateTimeKind 字段映射、输出映射与程序集引用检查；Lilara.Channel 无宿主/Data/Llm 引用。完整解决方案构建与测试通过：主程序 545、GraphRuntime 38、GraphSandbox 17，共 600 项，无失败/跳过。既有 NU1603/CA2022/SYSLIB0050 仍在；未验证真实模型/媒体/适配器，D5 拍前装载及 D1 全路径对拍仍未完成。

### Context：纯压缩阈值策略

CompressionPolicy/CompressionThresholds/CompressionTier 提取到 Lilara.Context，不依赖模型消息或宿主 AgentConfig；宿主 CompressionTierModule 保留构造接口及 EstimateChars，薄适配传入阈值。保持最小阈值优先、L3/L2/L1优先级、L1显式标记后不再提醒、L2/L3逐字提醒及L3进入/重入回调。新增11项边界与状态测试，修正映射测试可空断言；宿主构建、556项测试与diff检查通过。未验证真实模型压缩轮次；现有依赖/弃用警告仍在。

### Context：纯提示装配与变更意图

ContextAssembler/ContextMutations 两文件原文迁入 Lilara.Context/Context，保留命名空间、internal 与友元访问。GlobalPromptSections 仍由宿主组件容器提供。完整解决方案构建成功（0 错误、4 项既有警告），主程序 556、GraphRuntime 38、GraphSandbox 17，共 611 项测试通过；diff --check 通过。未执行真实模型或适配器冒烟，C/D 与完整信号迁移仍未完成。

### C1：Lilara.Adapters 消息契约首切

IAdapter、AdapterAction、AdapterInstanceConfig、IncomingMessage、OutgoingMessage、MessageAttachment、MessageSegment、AdapterStatus、IChannelAwareAdapter 九文件原文迁入 src/Lilara.Adapters，保留命名空间与可见性，友元宿主/测试；只依赖 Newtonsoft.Json。实现、factory/manager、ImageStorage 和 IEngineAwareAdapter 暂留宿主，仍需后续端口化。完整 sln 离线还原与构建退出码0（0错误、4项既有警告），主程序556、GraphRuntime38、GraphSandbox17，共611项测试通过，无失败/跳过；diff --check通过。未运行真实适配器收发或模型冒烟。

### Adapters：文件适配器媒体依赖注入

FileAdapter 实现迁入 Lilara.Adapters/File，以构造委托归档附件；宿主 AdapterFactory 注入原 ImageStorage.CopyToStorageAsync。保留附件原顺序、无效路径与归档失败跳过；独立构造未提供归档器时明确跳过附件。Adapters 增加 Logging 引用，无宿主回指。新增临时 JSON 文件入站的假归档集成测试（成功/失败/缺文件及未注入）；相关4项通过。完整解决方案离线还原、构建及613项测试通过（主程序558、GraphRuntime38、GraphSandbox17），diff检查通过。未运行真实适配器平台/模型；既有依赖替代及弃用警告仍在。

### Adapters：OneBot/Icalingua 实现迁移

新增 IAdapterMediaStore 下载/分类端口，宿主 AdapterMediaStore 代理 ImageStorage，AdapterFactory 注入。OneBot/Icalingua 构造可选媒体端口；未配置时图片沿现有 catch 路径跳过。两个平台目录迁入 Lilara.Adapters，沿用 SocketIOClient 4.0.5/BouncyCastle.Cryptography 2.4.0。发送路径只依赖 BotOutputParser 的 @ 占位符常量，现抽为 InlineMentionProtocol，宿主解析器共用同一值。新增四项假媒体解析回归，验证下载→分类顺序、sticker、图片占位与缺端口跳过。完整 sln 离线还原/构建及617项测试通过（主程序562、GraphRuntime38、沙箱17），diff检查通过；未连接真实平台或模型。既有4项构建警告仍在。
