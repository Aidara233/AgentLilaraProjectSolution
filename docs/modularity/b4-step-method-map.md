# B-4：`RunAsync` 拆方法 —— 阶段 → 方法名映射表

> 先定这张表，再动代码。426 行的主循环里有 6 处 `continue` / `break`，
> 机械抽取**几乎必然**改变控制流——这是本切片不能"搬文件"而必须"改结构"的原因。

---

## 0. 现状

`RuntimeSession.RunAsync`（`RuntimeSession.cs` **416–757**，约 **342 行**）。

`StepPhase` 五个粗档已在位，赋值点：`447`（Draining）、`637`（Inferring）、
`655`（ExecutingTools）、`694`（Judging）、`724`（Waiting）、`752`（Idle）。

循环骨架：

```
416  RunAsync(ct)
424    int agentRound = 0;
430    bool awaitingInput = _isRestored || _history.Count == 0 || Type == "express";
435    while (IsAlive && !ct.IsCancellationRequested)
437        bool hasComponentNotification = false;
438        if (awaitingInput) { ... }        ← 阶段 1–3
529        ── Agent 推理 ──                  ← 阶段 4
595            中途排空                     ← 阶段 5
630            模型调用                     ← 阶段 6–7
654        ── 工具执行 ──                    ← 阶段 8–10
727        else 无工具调用                   ← 阶段 11
744    finally { ... }                       ← 阶段 12
```

---

## 1. 六个控制流点（抽取的真正障碍）

| 行 | 语句 | 语义 | 抽取后必须保留的方式 |
|---|---|---|---|
| 441 | `break`（`WaitForActivation` 返回 false） | 退出 while（会话终止） | 方法返回 `StepAction.Stop` |
| 478 | `continue`（本轮无输入且无注入） | 回到 while 头重新等激活 | 方法返回 `StepAction.Restart` |
| 530 | `break`（`_agentCore == null`） | 退出 while | `StepAction.Stop` |
| 649 | `continue`（模型调用抛异常，已记 `[错误]`） | 进入下一迭代（同一突发内再调一次） | `StepAction.Next` |
| 715 | `break`（`endSession`） | 退出 while → finally 释放 claim | `StepAction.Stop` |
| 725 | `continue`（`endBurst`） | 回等待、保活 | `StepAction.Restart` |

**结论**：抽取的方法不能是 `void`，必须返回一个小枚举。

```csharp
internal enum StepAction
{
    Proceed,   // 继续本迭代的下一段
    Next,      // 结束本迭代，进入下一迭代（continue）
    Restart,   // 回等待（awaitingInput = true; continue）
    Stop,      // 退出循环（break）
}
```

---

## 2. 跨阶段局部变量（必须变成字段或出参）

| 变量 | 声明 | 跨哪些阶段 | 处置 |
|---|---|---|---|
| `agentRound` | 424 | 4 → 6（span 名、日志） | 提到 while 外，作为**参数**传给需要它的方法；或存 `_agentRound` 字段（更简单，与 `Phase` 同级） |
| `awaitingInput` | 430 | 1 → 判定 | 保留在 `RunAsync`，由返回的 `StepAction` 驱动 |
| `hasComponentNotification` | 437 | 2 → 4（避免重复通知） | 作为**入参/出参**在 2 与 4 之间传递，或由 `PrepareInference` 返回 `bool` |
| `roundSpan` | 630 | 6 → 8（`SetCloseDetail`） | **难点**：`using var` 跨两个阶段。建议改在 `InvokeModel` 内部开 span，`ExecuteTools` 只回报 detail，由调用方补写 |
| `invokeRequest` | 583 | 4 → 6 | 由 `PrepareInference` 返回，传给 `InvokeModel` |
| `roundCore` | 589 | 4 → 8 | 同上，随 `invokeRequest` 一起传出（`AgentCore` 引用） |
| `endBurst` / `endSession` | 697 | 9 → 10 | 由 `InterpretToolEffects` 返回一个 `ToolBatchOutcome(EndBurst, EndSession)` |
| `endReason` | 局部 | 12（finally 用） | 提到 while 外 |

⚠️ `roundSpan` 是唯一"跨阶段还带 `using` 生命周期"的东西，也是最容易抽错的一处。

---

## 3. 十二个方法（映射表）

命名遵循"阶段表"的顺序，全部为 `private`：

| # | 方法签名 | 对应行 | 职责 | 返回 |
|---:|---|---|---|---|
| 1 | `Task<StepAction> AwaitActivationAsync(CancellationToken ct)` | 438–452 | 等激活、`TouchActivity`、清 `WaitUntil`、`agentRound=0`、`_roundCount++`、`ApplyRoundPricing` | `Stop` / `Proceed` |
| 2 | `Task<bool> DrainEntryComponentNotificationsAsync()` | 454–471 | 突发入口的组件通知，注入 `[组件通知]` 并持久化 | 是否有通知（→ `hasComponentNotification`） |
| 3 | `Task<StepAction> CollectInputAsync(bool hasComponentNotification, CancellationToken ct)` | 474–528 | 第一次排空 + `BuildRoundAsync` + 游标推进 + 持久化（含无 host 的裸内容回退） | `Restart`（无输入）/ `Proceed` |
| 4 | `Task<RoundContext> PrepareInferenceAsync(bool hasComponentNotification)` | 529–592 | 组件通知补做、`_pendingInjections` 注入、**压缩提示 L1/L2**、**L3 锁定（发 mutation）**、构建 `invokeRequest`、锁 `roundCore` | `RoundContext(invokeRequest, roundCore)` |
| 5 | `Task DrainMidRoundAsync()` | 595–628 | 第二次排空（非阻塞）+ 持久化 | — |
| 6 | `Task<ModelOutput?> InvokeModelAsync(RoundContext ctx, CancellationToken ct)` | 630–651 | 开 span、`InvokeAsync`、影子快照；失败写 `[错误]` 并返回 null | null ⇒ `Next` |
| 7 | （并入 6） | 652 | assistant 消息入历史 | — |
| 8 | `Task<ToolBatchOutcome> ExecuteToolsAsync(RoundContext ctx, ModelOutput output)` | 654–693 | 工具执行 → 重写 assistant 为 native `tool_use` → `tool_result` → 持久化 | `(EndBurst, EndSession)` 初值 false |
| 9 | `Task<ToolBatchOutcome> InterpretToolEffectsAsync(ModelOutput output, IReadOnlyList<ToolResult> results)` | 699–709 | 逐个 `ToolEffectInterpreter.Interpret` 并应用动作，汇总 `EndBurst`/`EndSession`；`RequestWait` 置 `EndBurst` | 同上 |
| 10 | `Task<StepAction> DecideAsync(ToolBatchOutcome outcome)` | 710–726 | `endSession` → `Stop`；`endBurst` → 清 `_forceExitAttempts`、`awaitingInput=true` → `Restart` | `Stop` / `Restart` |
| 11 | `Task InjectContinueReminderAsync()` | 727–740 | 无工具调用时注入提醒并持久化 | —（之后 `Next`） |
| 12 | （保留在 `RunAsync` 的 `finally`） | 744–757 | 置 `Completed`、`Phase=Idle`、`lifeCtx.Close`、`OnCompleted` | — |

拆完后 `RunAsync` 本身应该只剩 **约 40 行**：声明状态 + while + 按 `StepAction` 分派 + finally。

---

## 4. 执行顺序（每步都能单独 build + test + 冒烟）

**不要一次拆 12 个**。按"依赖最少 → 依赖最多"排：

| 批次 | 拆哪些 | 为什么这个顺序 |
|---|---|---|
| **B-4a** | 11 `InjectContinueReminderAsync`（最独立，无返回值） | 先验证"抽方法 + 保持行为"这条路走得通 |
| **B-4b** | 9 `InterpretToolEffectsAsync`、10 `DecideAsync` | 二者构成"判定"闭环，一起拆才能验证 `StepAction` 设计 |
| **B-4c** | 8 `ExecuteToolsAsync` | 依赖 9/10 的产物类型 |
| **B-4d** | 6 `InvokeModelAsync`（含 span 改造） | `roundSpan` 的生命周期是唯一真风险 |
| **B-4e** | 4 `PrepareInferenceAsync` | 依赖 6 的 `RoundContext` |
| **B-4f** | 1 / 2 / 3 三段输入处理 | 改动最大（涉及 `awaitingInput` 与 `StepAction`） |
| **B-4g** | 5 `DrainMidRoundAsync` | 纯搬，最后做 |

每批之后：`dotnet build` → `dotnet test`（481 基线）→ **跑一次冒烟**（见 `implementation-log.md` 的配方）。

---

## 5. 验收判据

| # | 判据 | 怎么验 |
|---|---|---|
| V1 | 编译 0 error | `dotnet build` |
| V2 | 481 测试全绿 | `dotnet test` |
| V3 | **冒烟：会话仍能被创建并跑完一轮** | 隔离 Storage 投一条消息，看 `state.json` 的 `roundCount` / `status` |
| V4 | `StepPhase` 的 6 个赋值点仍在对应方法内 | `grep -n 'Phase = StepPhase'` 应落在拆出的方法里，而不是 `RunAsync` |
| V5 | 12 个阶段的执行顺序未变 | 冒烟日志里 span 顺序：`Session 轮次 #N` 在工具执行之前；`[组件通知]` 在 `[新消息]` 之前 |
| V6 | 6 个控制流点的语义未变 | 代码复核：`break` 全部 → `Stop`，`continue` 按"回等待 / 下一迭代"分别 → `Restart` / `Next` |

**V3 是这个切片的真正验收**——单测覆盖不到 `RunAsync`，只有冒烟能证明"会话还能跑完一轮"。
**V5 是防"抽错顺序"的唯一手段**：抽出方法后调用顺序可能被无意调换，而单测和编译都看不出来。

---

## 6. 风险与止损

| 风险 | 表现 | 止损 |
|---|---|---|
| `roundSpan` 的 `using` 被拆散 | span 提前关闭，耗时统计缺失（**不报错**） | B-4d 单独一批；冒烟后检查 `Logs/Model/` 里的 span 时长是否存在 |
| `endBurst`/`endSession` 语义混淆 | 会话该停不停、或提前释放 claim | B-4b 一起拆；冒烟看 `state.json` 的 `status` 与 `connectedChannelId` |
| `awaitingInput` 被抽进方法 | express 会话不再等输入，变成主动空转 | B-4f 最后做；冒烟看 `roundCount` 是否暴涨 |
| 抽取顺序被调换 | 模型调用先于上下文构建 → 上下文缺失 | V5 的 span 顺序检查 |

**一句话**：这个切片不能靠"等价性复核"证明安全（抽取本来就不是等价变换），
**只能靠 V3 冒烟 + V5 顺序**两道外部观测。所以每批都必须跑冒烟，不能只跑单测。

---

## 7. 未验证

- 上面 12 段的**行号是当前快照**（`RuntimeSession.cs` 767 行）。每批拆完都会移位，**动手前重新 `grep -n`**。
- `RoundContext` / `ToolBatchOutcome` 这两个新类型的字段清单是**按需推出来的**，实际拆的过程中可能发现还需要带别的东西（例如 `roundSpan` 的 detail）。
- 没有验证过"拆完之后 `RunAsync` 真的只剩 40 行"——那是估算，不是承诺。
