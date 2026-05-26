# 模块1审计报告：引擎核心 (Engine Core)

审计时间：2026-05-26
文件数：15 | 总行数：~2,500

---

## 发现问题

### 🔴 BUG — 中度

**1. MasterEngine._engineTasks 内存泄漏** (`MasterEngine.cs:120,599`) ✅ 已修复 2026-05-26
已完成引擎的 Task 对象永不移除，`_engineTasks` 列表随运行时间增长，无清理机制。
`WaitAllStoppedAsync` 每次都遍历全部历史 task。建议在 `HandleEventCoreAsync` 的清理环节（line 576）同步清理对应的 `_engineTasks`。

### 🟡 BUG — 轻度

**2. Schema 迁移 catch {} 吞没严重异常** (`MasterEngine.cs:244,256,267,279`) ✅ 已修复 2026-05-26
4 处 `catch { }` 空块原本只想忽略"列已存在"错误，但会吞没 DB 连接失败等严重问题。建议改为 `catch (Exception ex) when (ex.Message.Contains("duplicate column")) { }`。

**3. Gate._forceWake 竞态** (`Gate.cs:86-87`) ✅ 已修复 2026-05-26
`ForceWake()` 在另一线程设 `_forceWake = true` + `Signal()` 后，`RunAsync` 循环刚好执行到 `isForceWake = _forceWake; _forceWake = false;` 之间，flag 被意外清零。影响小（Signal 已发送下次仍会醒），但逻辑上不一致。可用 `Interlocked.Exchange(ref _forceWake, 0)` 修复。

### 🟠 设计问题 — 中度

**4. MasterEngine.InitAsync 单体过长** (`MasterEngine.cs:218-514`)
~200 行做 12 件事：DB 初始化、6 个 schema 迁移、6 个 Provider 初始化、3 个 Service 创建、CrossRequest 事件绑定、工具注册、插件加载、Component 系统、SpawnCheck 注册、自动启动引擎、MCP 初始化。建议拆成 `InitDatabaseAsync` / `InitProvidersAsync` / `InitServicesAsync` / `InitEngineSystemsAsync` 四个子方法。

### 🟢 ISSUE — 轻度

**5. LoopBus.Publish 静默吞异常** (`ILoopBus.cs:41-43`) ✅ 已修复 2026-05-26
handler 抛异常被 `catch (Exception) { }` 完全丢弃，查问题时没有任何线索。建议至少 `Signal.Warn` 记录。

**6. 过时注释** (`IAgentSession.cs:52`)
`WatchRule` 类有 "Phase 6 实现" 注释，如果 Phase 6 已完成应移除。

**7. SignalFilterManager 无线程安全** (`SignalFilterConfig.cs:54-64`) ✅ 已修复 2026-05-26
`_configs` 字典读写无锁保护。目前 WebUI 写 + 引擎启动读基本安全，但将来多引擎并发访问有风险。建议用 `ConcurrentDictionary` 或读写锁。

**8. 默认 system filter 缺少注释** (`SignalFilterConfig.cs:149-162`)
system 引擎的 WakeFilter 故意排除 ChannelMessage，但无注释说明 why。

---

## 正面发现

- **Agent.BuildToolInputJson 关键路径注释完善** (`Agent.cs:323-370`)：详细说明了 Anthropic API 要求 JSON object、数组会 400 的原因，是"关键位置注释"的正面范例
- **CrossRequestRegistry 持久化设计正确**：journal 追加+compact 的锁逻辑正确（已验证数据不丢）
- **EventBus / ModuleBus / DelegationBus 三层总线职责清晰**，耦合合理
- **Gate 的 delegate 注入模式**比继承更灵活，组合优于继承落实到位

---

## 判定

整体质量良好，无阻塞性严重 bug。2 个轻度 bug 建议修复，1 个内存泄漏建议尽快处理。设计层面 InitAsync 拆分可提升可维护性。
