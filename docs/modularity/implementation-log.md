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
