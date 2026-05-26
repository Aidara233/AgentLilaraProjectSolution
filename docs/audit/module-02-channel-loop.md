# 模块2审计报告：频道循环 (Channel Loop)

审计时间：2026-05-26
文件数：15 | 总行数：~3,200（含 ChannelEngine 1844行）

---

## 发现问题

### 🔴 BUG — 中度

**1. InitModules 中同步阻塞异步方法** (`ChannelEngine.cs:275-317`)
事件回调中大量使用 `.GetAwaiter().GetResult()` 调用异步方法（HandleSpeakToolAsync、HandleSendMediaToolAsync 等）。虽然在当前线程池环境下暂时不会死锁，但如果底层适配器依赖 SynchronizationContext，会死锁。此外异常传播路径不清晰。
```csharp
// 当前写法：
case "speak":
    HandleSpeakToolAsync(data).GetAwaiter().GetResult();
// 建议：改为 fire-and-forget 或同步回调只做轻量操作
```

**2. _bufferTriggered 的 TOCTOU 竞态** (`ChannelEngine.cs:338-361`) ✅ 已修复 2026-05-26
`EnqueueMessage` 在锁外读写 `_bufferTriggered`，而 `RunAsync` 在锁内重置它。两个线程可能同时操作导致：
- 一个消息刚触发了缓冲定时器，另一线程的 `RunAsync` 立即把 `_bufferTriggered` 清零
- 新消息错过了触发判断，被挂起到下一个缓冲窗口
影响中等：表现为偶发消息处理延迟。

### 🟡 BUG — 轻度

**3. Express 模式无跨轮次退避** (`ChannelEngine.cs:886-919`) ✅ 已修复 2026-05-26
Express 单轮有重试，但连续多个 gate cycle 失败不会累积退避。与 Agent 模式的 `BackoffSeconds` 不一致，极端情况下可能 spam API。

**4. ChannelExtractionWorker 的 fire-and-forget 无法等待** (`ChannelExtractionWorker.cs:73-99`)
`StartRun` 用 `Task.Run` 启动提取后不等待。如果引擎正在关闭，提取任务可能访问已释放的资源。`running` flag 和 `cts` 的赋值与检查之间有竞态窗口。

### 🟠 设计问题 — 重度

**5. ChannelEngine 构造函数代码重复 ~70%** (`ChannelEngine.cs:165-262`)
两个构造函数（`ChannelEngine(ctx, initialContext, initialMessage)` 和 `ChannelEngine(ctx, channel)`）共享 ~60 行几乎相同的代码（AgentConfig、persistence.LoadContext、Gate、extractionWorker）。区别仅在：冷启动路径无 incoming message、无参与者更新。

```csharp
// 建议提取共享初始化：
private void InitChannel(ISystemContext ctx, Database.Channel channel) { ... }
```

**6. ChannelEngine 单体过长 — 1844 行**
单文件包含：消息缓冲、冲动值、Express/Working 双模式执行、上下文建造（BuildStartInjectAsync 120行 + BuildRoundInjectAsync 140行）、工具回调（14种工具 case）、记忆检索、参与者管理、持久化、图片处理、关注规则、快照导出。建议至少拆分为：
- `ChannelEngine.cs` — 循环骨架 + 模式执行
- `ChannelContextBuilder.cs` — BuildStartInjectAsync + BuildRoundInjectAsync
- `ChannelToolHandler.cs` — 14 个工具回调

### 🟢 ISSUE — 轻度

**7. DeserializeMessages 双重序列化** (`ChannelContextPersistence.cs:133-134`) ✅ 已修复 2026-05-26
`JsonConvert.SerializeObject(dynamic) → JsonConvert.DeserializeObject<T>()` 绕了一圈。`dynamic` 对象实际是 `JToken`，直接用 `((JToken)obj).ToObject<List<Message>>()` 即可。

**8. LoopGate 缺少关键注释** (`LoopGate.cs:26,34`)
`_signalPending` 的 CAS 模式正确但微妙，缺注释说明两次检查的必要性。

**9. BuildStartInjectAsync 双格式分支缺注释** (`ChannelEngine.cs:1095-1119`)
Working 用 XML `<conversation_history>`、Express 用纯文本 `[对话历史]`，但没有注释说明为什么格式不同。

**10. Express 文本丢弃逻辑缺说明** (`ChannelEngine.cs:969-975`)
Express 模式丢弃模型直接文本输出的原因是"防止绕过工具系统"，这个设计决策应该用注释标注，否则读者会困惑为什么丢弃。

**11. HandleAlertAsync 中 alerts 直接修改 Person 对象** (`AlertHandler.cs:18-66`)
AlertHandler 是静态方法，直接修改传入的 `Person` 对象属性，然后调用 `UpdatePersonAsync` 持久化。如果多处同时持有同一个 Person 引用会导致不一致。当前实践中还好（Person 刚从 DB 查出），但耦合隐晦。

---

## 正面发现

- **LoopGate 的 lock-free 设计**精巧：`_signalPending` + CAS 正确解了 auto-reset 信号丢失问题
- **ChannelContextPersistence 用 .tmp + Move 原子写入**避免文件损坏
- **ImpulseTracker 的指数衰减公式**简洁清晰
- **BotOutputParser 的 at/reply 标签设计**合理：`\x01` 分隔符在正常文本中不会出现
- **Express/Working 自适应切换**设计灵活：escalate/deescalate 工具 + 积压自动回退

---

## 判定

最大问题是 **ChannelEngine 1844 行单体过大**，建议拆分。2 个中度 bug（同步阻塞、TOCTOU 竞态）应修复。整体代码质量好，复杂度主要来自业务需求的多样性。
