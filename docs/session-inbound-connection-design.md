# Session 入向接通 + 生命周期 + 撤子模式 设计（第二批）

> 状态：**已实现（2026-06-24）**。
> P1 撤子模式随减法批完成（master 17d0000）；A+B+游标接力在分支 `feature/session-inbound-pipe`（base=master 2f16794）3 commit 落地。
> 6109786(A生命周期) / 03d4d90(B入向接通) / 0a44334(游标接力)。build 0错误 + 15单测过 + --test 启动正常。E2E 留发行版手动。
> 实现要点见 `.git/sdd/progress.md` 末段「P0 入向接通完成」。一处偏差：B 分叉点落在 `ChannelEngineSpawnCheck.HandleMessageAsync` 内（OnMessageAsync 入库后、ChannelEngine spawn 前），非设计原文的 RouteMessageAsync——避免双重入库，功能等价。

## 问题回顾（已用真实代码核实）

### A. session 一轮即死（P0 直接原因）
`RuntimeSession.RunAsync` 只有一个 `while (IsAlive)` 循环身兼两职：首轮处理 `BuildStartAsync` 的初始消息，后续轮 `WaitForActivationAsync` 等端口。突发结束（`wait`/`deescalate`/`switch_mode`/无工具）全用 `break`，但 `break` 跳出的是**整个循环** → 落入 `finally` → `Status=Completed` → SessionEngine.finally 调 `ReleaseSessionClaims` → claim 释放 → 频道裸奔 → 下条消息走老路喂 ChannelEngine → 453 行查 claim 已空 → 不跳过 → **Express 接手**。

`wait` 调了 `SetWaitUntil`（意图"挂起等下轮"），但紧跟 `break` 让 WaitUntil 永远没机会被检查。设计意图与实现直接矛盾。

### B. 入向从没接到 session（P0 根本原因）
入向唯一真路：`适配器 → MasterEngine.RouteMessageAsync → ChannelEngineSpawnCheck.HandleMessageAsync → ChannelEngine.EnqueueMessage`。喂 session 端口的唯一入口 `MessageEngine.AcceptIncoming`（内部 `Distribute → MessageInputPort.Enqueue`）**全仓零调用**。所以即便修好 A、session 活着握 claim、ChannelEngine 乖乖跳过，session 端口也永远收不到消息，只能在 WaitUntil 超时上空转。

### 游标 split-brain（第二批头号约束）
- ChannelEngine：游标存 `ChannelContextPersistence`（per-channel JSON），每轮 `SaveContext` 写回。被 claim 跳过时不推进、不保存——冻结在 session 接管前。
- SessionAgentHost：游标在 `_states[cid].LastConsumedMessageId`（纯内存，从不持久化），且 escalate session 走"忽略游标取最近 N 条"，游标实际没推进。

后果：session 消费了消息却不更新那份"频道级唯一真相"，session 一死 ChannelEngine 从旧游标恢复 → 重读 session 期间消息当新消息。用户硬约束："我可不希望一切回 express，结果之前的消息全成新消息了。"

---

## 设计

### A. 生命周期：突发结束 ≠ 会话结束

`RunAsync` 内层拆出"一次突发"语义。突发结束分两类：
- **挂起类**（`wait` / 无工具 / `switch_mode`）：清空 `messages`，`continue` 回到 `WaitForActivationAsync` 等待。session 保活，claim 保留。
- **终结类**（`deescalate` / 超限 / 余额耗尽 / `RequestStop`）：真正 `break` 退出循环，走 finally 释放 claim。

改法：引入 `bool endSession` 标志区分两类。把现有 `stopRequested` 语义拆成 `endBurst`（回等待）和 `endSession`（退出）。
- `wait` → `SetWaitUntil` + endBurst（**不** endSession）
- 无工具 → `messages.Clear()` + endBurst（已是此意，保留）
- `deescalate` → endSession
- 内联 wait（`RequestWait`）→ endBurst
- 超限（agentRound > MaxAgentRounds）→ endSession

endBurst 时 `messages.Clear()` 后 `continue`；endSession 时 `break`。

⚠️ **WaitUntil 超时的活性**：session 挂起后只有两种唤醒——端口来消息（B 接通后才有）或 WaitUntil 到期（`WaitForActivationAsync` 已支持）。A 单独上线时端口仍空，session 会一直挂到 IdleTimeout 自然结束——比"一轮即死回 Express"好，但仍非正常。**所以 A 必须与 B 同批**，单 A 无意义（已与用户确认）。

### B. 入向接通：claim 频道改投 session 端口

**分叉点 = `MasterEngine.RouteMessageAsync`**（入向唯一总闸，eventLock 串行化）。在 `chCheck.HandleMessageAsync` 之前插入判断：

```
RouteMessageAsync(msg):
  ① CommandSpawnCheck（不变）
  ② 查 SessionEngine.GetFullClaimer(channelId)
     - 有 claimer → 走 session 入向路径（见下），return，不碰 ChannelEngine
     - 无 claimer → chCheck.HandleMessageAsync（原路）
```

**session 入向路径**需要：入库 + 去重 + 投递到 session 端口。`MessageEngine.AcceptIncoming` 已实现入库+去重+Distribute，但它吃 `EngineIncomingMessage`，而 RouteMessageAsync 拿的是 `IncomingMessage`。两条路：

- **路 B1（复用 AcceptIncoming）**：RouteMessageAsync 把 `IncomingMessage` 转 `EngineIncomingMessage`，调 `MessageEngine.AcceptIncoming`。但 AcceptIncoming 的入库字段比 `SessionManager.OnMessageAsync` 少（无 SenderName 解析、无 Person/User 映射、无信任升级）。会丢身份信息。
- **路 B2（先入库再投递）**：RouteMessageAsync 仍调 `ctx.Session.OnMessageAsync(msg)` 完成完整入库+身份映射（与 ChannelEngine 路一致），再把结果投到 session 端口。投递通过 MessageEngine 新增方法 `DistributeToClaimedSession(channelId, msg)` 或直接 `Distribute`。**推荐 B2**，身份信息完整，与现有入库逻辑一致。

**身份信息断层修复**：`DrainInputPortsRaw` 现在把 `EngineIncomingMessage → UserMessage` 时 `SenderName=""`（FormatIncoming 渲染空名）。B2 路投递时应携带 SenderName。两个办法：
- 投递 `EngineIncomingMessage` 时补 `SenderName` 字段（需给 `EngineIncomingMessage` 加该字段）
- 或投递已入库的 `UserMessage`（含完整身份），`DrainInputPortsRaw` 直接消费 UserMessage 分支

倾向后者：session 端口数据类型本就是 `object`，直接塞 `UserMessage`，`DrainInputPortsRaw` 加一个 `is UserMessage` 分支，绕开 EngineIncomingMessage 的字段缺失。

### 游标接力：频道级唯一真相

**核心**：session 占有期间，消费消息后推进**同一份** `ChannelContextPersistence` 游标。session 结束时游标已最新，ChannelEngine 恢复时 `LoadContext` 读到的就是 session 推进后的位置 → 无缝接续，零重读。

落点：
- session 每轮 `DrainInputPortsRaw` 消费消息后，取本轮最大 message id，写回该频道的 `ChannelContextPersistence.SaveContext`（只更游标，summary/mode 保持）。
- SessionAgentHost 的 `_states[cid].LastConsumedMessageId` 降级为内存镜像，权威源是 persistence。
- escalate session 首轮"取最近 N 条"时，把这 N 条的最大 id 作为游标起点写回 persistence（否则首轮消费的历史不算进游标，ChannelEngine 恢复会重读）。

⚠️ **claim 建立与游标快照的时序**：escalate 创建 session 时（CreateSession 同步 claim），ChannelEngine 此刻可能正跑到一半。需确认 claim 建立瞬间 ChannelEngine 的游标已 flush，session 从该点接力。ChannelExpressCycle:240 每轮 SaveContext，escalate 那轮自身会 SaveContext 后再 break（ChannelExpressCycle:204 escalateTriggered break）。需核实 escalate break 前是否已 SaveContext —— **实现前必须确认这个时序**。

### P1. 撤旧 working 子模式

足迹（已 grep 定位）：
- `RuntimeSession.cs:268` `_agentCore.CurrentModeId = MaxLevel>0?"build":"plan"` → 删，固定 null 或不设
- `AgentCore.cs:178/244` CurrentModeId 模式过滤 → 查清后简化（session 不再分 plan/build）
- `SwitchModeTool`（`GetModesByMetaType("Working")`）→ 整个工具下线
- `RuntimeSession.cs:335` 刚加的 `switch_mode` case → 删
- `CreateSessionSpec.MaxLevel` / `RuntimeSession.MaxLevel` / `SetLevel` → 语义简化（escalate 仍传 MaxLevel=1，但不再映射子模式；保留作工具等级闸或一并清理，待定）
- `ModeConfig.json` 的 plan/build 模式定义 → 清理（保留 express + working 两态）

P1 与 A 同动 session 循环+模式语义，合并做。

---

## 实现顺序（建议）

1. **P1 先做**（独立干净）：撤子模式，简化 CurrentModeId，删 switch_mode。缩小后续改动面。
2. **A 生命周期**：拆 endBurst/endSession。此时单测可验"wait 不退出循环"（需给 RunAsync 可测性，或留 E2E）。
3. **B 入向接通**：RouteMessageAsync 分叉 + session 端口投递 + DrainInputPortsRaw 吃 UserMessage。
4. **游标接力**：session 推进 persistence 游标 + escalate 首轮游标快照 + 时序核实。
5. 全量 build + test + 发行版 E2E（escalate→session→新消息→session 接手→deescalate→ChannelEngine 无缝接续，零重读）。

## 待用户确认（开工前）

1. **B 路选择**：B2（先 OnMessageAsync 入库再投 UserMessage 到端口）—— 确认采用？
2. **游标接力时序**：escalate break 前 ChannelEngine 是否已 SaveContext，需我先读 ChannelExpressCycle 尾部确认；若没有需补一次 flush。
3. **MaxLevel 去留**：撤子模式后 MaxLevel 是否还作"工具等级上限"用途，还是一并清理？
4. **拆分策略**：4 步是否拆成独立 commit 逐步验证（推荐），还是合并提交。
