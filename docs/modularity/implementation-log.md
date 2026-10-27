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
| **H2：step 不再直写 `_agentCore.EngineType` / `AdditionalTools`** | 要改 `AgentCore.InvokeAsync` 的公开面，而 `Engine/Core/Agent.cs` 是第二个调用方，两处会短暂分叉 | 单独一笔，与 B 阶段第一步一起（那时 `step` 才有独立文件可对照） |
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

## 下一步

| 顺序 | 内容 |
|---|---|
| 1 | **A4-3（H2）**：`llm.invoke(request)` 入参化，去掉对 `_agentCore` 字段的直写 |
| 2 | **B**：session 拆 20 部件（切文件，不切 csproj） |
| 3 | **C**：拆 16 个 csproj |
| 4 | **D**：影子图对拍 → 内置图 JSON 化 |

B 的前置条件已经具备：`step` 的 12 阶段表、`ToolEffectInterpreter` 纯函数、`ISnapshotPort` 的设计（D3 定为扁平 payload）。
