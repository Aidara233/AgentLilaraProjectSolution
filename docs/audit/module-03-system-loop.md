# 模块3审计报告：系统循环+子Agent (System Loop)

审计时间：2026-05-26
文件数：10 | 总行数：~2,100

---

## 发现问题

### 🔴 BUG — 中度

**1. TaskSession.SendInstructionAsync 永远返回 false** (`TaskSession.cs:63-67`) ✅ 已修复 2026-05-26
`Start()` 方法在 `initialInstruction` 写入后立即 `instructionQueue.Writer.Complete()`，导致后续 `SendInstructionAsync` 写入失败。子 agent 无法接收追加指令，`IAgentSession.SendInstructionAsync` 的完整语义未实现。系统循环通过此方法下发指令的场景会静默失败。

**2. CompressSyncAsync 同步阻塞异步** (`SystemEngine.cs:143-155`)
构造函数回调中 `.GetAwaiter().GetResult()` 调用 `CompressSyncAsync`（实际为 `CompressAsync`），在构造线程上同步阻塞异步 IO（SummarizationCore 的模型调用）。如果初始化线程有 SynchronizationContext，会死锁。

### 🟡 BUG — 轻度

**3. ContextCompressionModule 死代码** (`ContextCompressionModule.cs:18`) ✅ 已修复 2026-05-26
`summarizationCore` 字段创建但从未使用。压缩逻辑已全部移入 `CompressionTierModule`。

**4. CompressSyncAsync 命名误导** (`CompressionTierModule.cs:118-120`) ✅ 已修复 2026-05-26
方法叫 `_Sync` 但实际直接 return `CompressAsync` 的 Task，不做同步等待。同步阻塞是由调用方（SystemEngine 构造函数）做的。名实不符。

**5. subAgents 字典泄漏** (`SystemEngine.cs:762-771`) ✅ 已修复 2026-05-26
死掉的子 agent 只在 `GetActiveSubAgents()`（WebUI 查询）时清理。若长时间无人查看 WebUI，死 agent 对象累积。

### 🟠 设计问题 — 中度

**6. TaskSession.BuildMessages 每轮重复注入 system status + tool descriptions** (`TaskSession.cs:183-226`)
每轮都重新构建并注入相同的系统状态和工具描述，浪费 token 且语义上应只在首轮注入一次。

**7. GetLastSleepTimeAsync 跨模块文件依赖** (`SystemEngine.cs:901-935`)
SystemEngine 直接读取 DreamEngine 的 `DreamStats.json`，格式变更会静默破坏。应通过接口或共享存储层访问。

**8. FindAdminChannelsAsync 返回全部频道（TODO）** (`SystemEngine.cs:981-991`)
注释承认"返回所有频道（管理员可能在任何频道）"是简化实现，但实际上是将所有频道的 loop 都通知了。应实现真正的管理员频道查找。

**9. SystemEngine 构造函数过长** (`SystemEngine.cs:103-171`)
~70 行做：模块初始化、AgentConfig、CompressionTierModule（含内联回调）、Agent 创建、上下文恢复、Gate 创建。建议拆出 factory 方法。

### 🟢 ISSUE — 轻度

**10. CommandSpawnCheck._sessions 无锁但依赖外部序列化** (`CommandSpawnCheck.cs:23`)
`Dictionary` 无锁保护，依赖于 `MasterEngine.eventLock` 的序列化保证。缺注释说明这个依赖。

---

## 正面发现

- **SystemEngineSpawnCheck 自愈设计**合理：10s cooldown + TimerEvent 触发重启
- **PendingEventsModule 简洁清晰**：SetPendingCrossRequests → BuildRoundInjectAsync 单向数据流
- **ContextPersistence WAL 模式正确**：JSONL 追加 + 版本检测 + 旧格式清空
- **DelegationNotificationModule 格式化注入**干净：过滤器 + 中文状态映射
- **TaskDoneTool 设计精巧**：临时注册/注销，`_taskDoneSignaled` flag 避免竞态

---

## 判定

1 个中度 bug（SendInstructionAsync 永远返回 false）需修复。3 处死代码/命名问题应清理。跨模块文件依赖（DreamStats.json）建议改为接口。
