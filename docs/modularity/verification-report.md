# 验证报告：23 项已定，8 项等你敲结论

> 对象：`AgentLilaraProjectSolution`（主动）+ `AgentLilaraCode`（参照）。
> 全部为静态阅读取证，**未 build / 未 run / 未 test**。
> 本报告关闭的项已在 `modules.json` 的 `resolved` 里登记。

---

## 第一部分：已验证并关闭（23 项）

### 归属与职责

| 原问题 | 结论 | 证据 |
|---|---|---|
| **Q1** `MCP/` 归谁 | **`plugin-host`** | 4 文件是 MCP **客户端**：`McpBridgeTool`（把外部 server 的工具桥成 `ITool`）、`McpServerManager`/`McpServerConnection`（连接管理）、`McpConfig`；依赖 `ModelContextProtocol.Client`。它和插件一样是「工具来源」，不是协议接入 |
| **Q2** `Models/` 归谁 | **`llm`**（我原先判 `data`，**错了**） | 3 文件是 LLM 协议类型：`Message`/`Delta`/`StreamEvent`/`ContentPart`/`Usage`。消费方是 `NativeToolMessageFormatter`、`LegacyRequestMapper`、`CompressionTierModule`、`SessionAgentHost` —— 全是 LLM 侧，无一是仓储 |
| **Q4** console-client 单根还是森林 | **森林**（独立应用） | `.csproj` 零 `ProjectReference`、零 `PackageReference`，只有 `TargetFramework net8.0`；4 文件（`ChatSession`/`HttpApi`/`WsClient`/`Program`）走 HTTP/WS |
| **Q6** webui API 粒度 | **按 Provider（页）分资源** | `Shell/DataSourceManager.cs` 注释：每个 `DynamicPage` 实例持有一个，负责**请求取消、过期结果丢弃、幂等订阅与释放**；有状态数据源通过 `DataSourceDefinition.Factory` 在页面初始化时创建。鉴权统一：`WebAuthService.ValidateCredentials` + `CreatePrincipal` |
| **Q7** WebUI 隐藏依赖 | **无**（除已知的 `ProviderRegistry`） | 全仓 `using AgentCoreProcessor.WebUI` 只在 `MasterEngine.cs:99`、`PluginLoader.cs:25,35`、`Program.cs:9,104-117`。`Engine`/`Tool`/`Adapter` 不引用 `WebUI.Services` |
| **Q8** 三个总线的关系 | **实际两套在用，`ILoopBus` 是残留** | `Engine/Core/ModuleBus.cs:6` 注释：「每引擎独立的模块间通信总线。**替代 ILoopBus + ComponentEventBus**」。`ModuleBus` 用于 Component 层（5 处）；`EventBus` 28 处（全局）。`ILoopBus` 只剩 `EngineModule.Attach`(:43) 与 `LoopControlModule.Attach`(:25) 两处空实现 |
| **Q11** `Notify` 与 `SendMessage` 冲突吗 | **不冲突，两条通路** | `Notify` → `ExternalEventDispatcher.cs:91`（无 Notify = 未命中，静默不打扰）；`SendMessage` → `IngressIntentExecutor.cs:99`、`MessageGraphIntentDispatcher.cs:25` |

### `step` / `turn` 与生命周期

| 原问题 | 结论 | 证据 |
|---|---|---|
| **Q3** `session`/`turn` 切分线 | **已定**：`step` = 突发（N 次模型调用）；`turn` = 一次模型调用（`step` 内部，不对外） | `:986` while 是突发循环；`:1003` 突发开始重置 `agentRound`；`:1006` `_roundCount++` 是**突发数**；`:1144` `agentRound++` 是**调用序号**；`:1279` 无工具调用不 break |
| **Q15** `turn` 的 `Activated` 会不会成为热点 | **不会** | `Activated` 是 **per-burst**（`:1003`/`:1006`），不是 per-call。频率 = 突发数 |
| **Q16** 组件钩子与拍边界重合吗 | **不重合** | `OnBeforeInvokeAsync` 在 `:1011` 与 `:1085`（**per-turn**，两处调用点，有 `hasComponentNotification` 守卫防重复）；`Activated` 是 per-burst |

### `session` 的三个待定细节

| 原问题 | 结论 | 证据 |
|---|---|---|
| **Q28** `CreateSession` 156 行里还藏着什么 | **9 步线性装配脚本，不宜再拆** | profile → `ComputeFinalComponents` → `new RuntimeSession` → 订阅完成事件 → 加载组件 → 注入 Core/MessageEngine → 端口订阅 → `AutoClaim` → 建 `SessionAgentHost` → 跑 `InitActions` → 写 `spec.json` → 启用压缩 → 起 `RunAsync` → 池登记。它**本身就是组合根在会话层的投影**，归 `session.assembly`（Singleton） |
| **Q29** `_forceExitAttempts` 语义 | **「两次机会」逃生口，保留** | 声明 `:108`；`:633` 检查（>0 → 发「强制退出」文本 + `EndSession`）；`:639` 递增；`:1272` `endBurst` 时重置。首次遇 in-flight 资源 → 拒 + 警告 + 计数 |
| **Q30/Q33** `SessionEngine.RunAsync` 113 行搬不搬 | **不搬，拆成 tick 分发器 + 5 个订阅者** | 30 秒 tick 里混了 6 件事：express 空闲回收(`:21`)、WaitUntil（实际由 `RuntimeSession` 自处理）、`ScheduledTask`+Recurring cron(`:28-53`)、Express 提取空闲补(`:55`)、TODO 每 15 分钟(`:58`)、Review 会话孵化(`:77`) |

### 持久化与归档

| 原问题 | 结论 | 证据 |
|---|---|---|
| **Q26** `LoadHistory(maxLines=1000)` 是缺陷吗 | **不是，有意为之** | 注释明说「只读取尾部最近 N 行（超大 history 全量读取会膨胀内存；尾部是最近上下文，优先保留）」；另有崩溃半截处理（末尾 `assistant` 含 `tool_use` 但无配对 → 丢弃）与损坏行跳过 |
| **Q27** `ArchiveAsync` 空实现 | **有意，且零调用点** | 注释：「留档（Phase 1-6 范围内为空实现，不删除存档目录）」；全仓 grep 仅定义处，无调用 |
| **Q23** `Reclaim` 与 `ContextCompaction` 是两层吗 | **是两个独立机制，在 `Stepper` 里先后配合** | `ContextCompaction.Project(store, handle)` → `CompactionProjection(Checkpoint, Suffix)`；`Reclaim.Resolve(visible, blobs, ...)` 决定释放哪些结果。顺序：`RecordRelease` 先落盘 → `Reclaim.Decide` 只读已落盘的线；`Compaction` 投影**排除 `ReclaimEvent`**(`:52`)。`Reclaim` 作用在**投影后的可见后缀**上 |

### GraphRuntime 的缺口

| 原问题 | 结论 | 证据 |
|---|---|---|
| **Q13** `ModuleDescriptor` 支持 Scope/Instance 吗 | **不支持** | `ModuleDescriptors.cs` 的 `record` 只有 `MethodDescriptor`/`PropertyDescriptor`/`ModuleDescriptor`，无 Scope/Instance 字段；`ModuleDescriptorGenerator` 只遍历 `GetMethods`(:53) + `GetProperties`(:101) |
| **Q14** 多实例下读哪个 property | **已确认**：构造时抓单实现，实例图模型下恰好正确 | `ModuleNodes.cs:227,240,256` 构造时捕获 `_implementation` |
| **Q19** `ClassId` 有守护吗 | **没有，而且不一致** | `GraphManager._classes` 三处赋值：`:322` **直接赋值无 ContainsKey**（静默覆盖）；`:432`、`:641` 有检查。无全局注册表 |
| **Q21** 有优雅退出吗 | **有，但绑在 Sdk.Web 上** | `Program.cs:473` `lifetime.ApplicationStopping.Register(...)`；`:484` `ShutdownComponentsAsync().GetAwaiter().GetResult()`（同步阻塞）；`:493-494` Dispose。webui 独立后 `lifetime` 要换 |

### mind 可拆性

| 原问题 | 结论 | 证据 |
|---|---|---|
| **Q32** `mind` 拆 6 个可行吗 | **可拆 5 个域 + 壳，但有 3 条内部依赖要端口化** | `Writer` 构造注入 `ConflictService` + `EmotionService` → 依赖 ①②；`Resurrector` 持 `MindEngine`（取 `Writer`）→ 依 ③；`ExpressExtractor` 已依赖 `IWriterAccess`**接口**（好）。`ConflictService` 注释「由 `Writer` / `MindEngine` 持有使用」→ 应归 `mind.write` |

### 频道双存储

| 原问题 | 结论 | 证据 |
|---|---|---|
| **Q5** 亲和度到底几个存储 | **两个，json 优先、DB 兜底** | `Database/Channel.cs:12` `Affinity`（默认 1.0）；`ChannelStateManager.LoadConfig(channelId, dbAffinity)` 读 `ChannelState/{id}.json`，缺失时用 DB 值；**唯一调用点** `WebUI/Providers/ChannelProvider.cs:211`。注意 `ChannelContextPersistence` 存的是 `summary/mode/rounds/cursor/escalateReason`，**不含 affinity** |

---

## 第二部分：需要你敲结论（8 项）

| # | 决策 | 选项 | 我的倾向 | 代价 |
|---|---|---|---|---|
| **D1** | 复合模块的 `[Module]` 标在哪 | ① interface（内部图作为实现细节）② class | **①** —— 与现有 `AttributeTargets.Interface` 一致，`ModuleHost.Bind` 不用改语义 | ① 需要额外约定"这个 interface 的方法由内部图实现" |
| **D2** | 实例图要不要支持嵌套引用另一类型图 | ① 支持 ② 不支持（只允许一层） | **② 先不支持** —— 会话已嵌一层（`session → step → context/mind/vision`），再嵌会让"有效图 = 类型图 ⊕ overlay"变成递归，校验复杂度陡增 | ② 未来要嵌得改校验器 |
| **D3** | 实例图 overlay 的持久化格式 | ① 扩 `SessionPersistence` 三文件（spec/state/history）② 新增第四个文件 ③ 只存扁平字段，不存节点/连线增量 | **③** —— 现有 `SessionStateSnapshot` 14 个扁平字段已够，真实需求是"变量值"，不是"结构差异" | ①② 都要写迁移（`StateFormatVersion` 当前是 2） |
| **D4** | `swapClass` 热换时正在拍内的实例怎么办 | ① 等拍结束 ② 打断 ③ 拒绝（有活跃拍时不许换） | **①** —— 与 `GraphInstance.Run` 的"失败零副作用"一致；③ 最安全但会让热换不可用 | ① 要加"当前拍完成"信号 |
| **D5** | `channel.context` 在拍内做 IO —— 要不要改 | ① 改成拍前装载（严格遵循"拍内纯求解"）② 允许拍内 IO（放宽原则） | **①** —— 但这是**行为改动**（现有 `:1032` `await _host.BuildRoundAsync` 就在拍内） | ① 改动大，要引入"装载阶段"；② 原则破例，图就不再是可静态求解的 |
| **D6** | 频道亲和度以谁为权威 | ① json ② DB 列 ③ 统一到 `IChannelStore` 端口（两个实现二选一） | **③** —— 已写进 `module-catalog.md` 的修正 | ①② 都要保留双写逻辑，而双写迟早分叉 |
| **D7** | `mind` 拆几个 | ① 5 域（Conflict 归 `write`）② 6 域（Conflict 独立）③ 不拆，整块保留 | **①** —— `ConflictService` 注释自己说"由 Writer / MindEngine 持有"，独立出去反而要两处引用 | ③ 保持 2340 行单模块 |
| **D8** | 插件 `Stable` 约束要不要可验证 | ① 保持作者契约 ② 加可验证（如要求 System/Primary 贡献的正文在会话内不变） | **① 先保持** —— ALC 明确说"稳定性目前是作者作者契约，宿主不通过执行隔离证明" | ② 要做正文哈希比对，且要定义"会话内"的边界 |

---

## 第三部分：本报告新发现的两点（不是原问题）

### N1 `ILoopBus` 是半迁移残留

`ModuleBus.cs:6` 声称"替代 ILoopBus + ComponentEventBus"，但 `ILoopBus` 接口还在，且 `EngineModule.Attach`(:43) / `LoopControlModule.Attach`(:25) 仍是空实现。**拆模块时它应该被删掉**，而不是搬进新结构。

### N2 `GraphManager._classes` 的三处赋值不一致

`:322` 直接赋值（静默覆盖），`:432`/`:641` 有 `ContainsKey` 检查。同一份注册表三种写法 —— 这是 U9（`ClassId` 唯一性）缺失的具体表现，也是 `swapClass` 的安全隐患。

---

## 第四部分：验证方法与边界

- 全部靠 `grep` / `sed` / `head` 读源码，**没有编译、没有运行任何测试**。
- 已关闭的 23 项里，都指到了具体文件与行号；其中 Q28（156 行）与 Q30（113 行）是**读过正文**的，Q3/Q15/Q16 是**通读 986–1305 全文**后定的。
- `AgentLilaraCode` 侧只读了 `Axes.cs`、`SystemBlock.cs`、`FloatingContextTool.cs`、`PromptContribution.cs`、`Stepper.cs`（结构与关键段）、`Reclaim.cs`/`ContextCompaction.cs`（头部与互引）、`PluginHost.cs`（相关段）、`docs/07-轴插件接入.md`。**未读 `Stepper.StepAsync` 的 506 行正文**。
- 因此 D1–D8 是**判断与权衡**，不是阅读能解决的；若你要更硬的依据，只有几条路：编译、跑测试、或继续读 `StepAsync` 全文。
