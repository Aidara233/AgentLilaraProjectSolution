# 决策定稿（D1–D8）

> 状态：**已定稿**。全部按 `verification-report.md` 里记录的倾向采纳。
> 这 8 项是**判断与权衡**，不是阅读能解决的——所以它们是结论，不是待查项。
> 依据：`verification-report.md` 的 23 项验证 + 六份设计文档。

---

## 0. 一页速览

| # | 决策 | 定论 | 一句话理由 |
|---|---|---|---|
| **D1** | 复合模块的 `[Module]` 标在哪 | **interface** | 与现有 `AttributeTargets.Interface` 一致，`ModuleHost.Bind` 语义不动 |
| **D2** | 实例图是否嵌套引用另一类型图 | **先只允许一层** | 递归会让「有效图 = 类型图 ⊕ overlay」的校验复杂度陡增 |
| **D3** | 实例图 overlay 持久化格式 | **只存扁平变量值，不存结构增量** | 现有 `SessionStateSnapshot` 14 字段已够；真需求是变量值不是结构差异 |
| **D4** | `swapClass` 时正在拍内怎么办 | **等拍结束** | 与 `GraphInstance.Run` 的「失败零副作用」同一条原子性 |
| **D5** | `channel.context` 拍内 IO | **改成拍前装载** | 保住「拍内纯求解」，否则图不再可静态求解 |
| **D6** | 频道亲和度权威 | **统一到 `IChannelStore` 端口，单实现** | 双写迟早分叉；现状就是 json 优先 + DB 兜底 |
| **D7** | `mind` 拆几个 | **5 域 + 壳并入 host** | `ConflictService` 自述「由 `Writer` / `MindEngine` 持有」 |
| **D8** | 插件 `Stable` 可验证性 | **保持作者契约** | ALC 明确：宿主不通过执行隔离证明正文稳定 |

---

## 1. 逐项展开

### D1 · `[Module]` 标在 interface

**定论**：`[Module("session.step")]` 继续标在 **interface** 上。复合模块用**另一个** attribute 声明内部图，而不是把 `[Module]` 挪到 class：

```csharp
[Module("session.actor", Scope = ModuleScope.PerSession)]
[Composite(ClassId = "builtin.session")]        // 新增，仍标在 interface 上
public interface ISessionActor { /* 对外端口 */ }
```

**理由**：`ModuleHost` 与 `ModuleDescriptorGenerator` 全部基于接口反射（`GetMethods`/`GetProperties`）。挪到 class 会让「模块 = 接口」这条已有约定断裂，而 `Bind(implementation)` 的校验也依赖 `IsInstanceOfType(interface)`。

**约束**：
- `Composite` 只声明 `ClassId`，不声明端口——对外端口就是 interface 的方法与属性（U7 断言：内部图公开端口 ⊆ interface 端口）。
- 复合模块的方法实现由内部图的 `sink` 承担；`ModuleHost.Bind` 对这种模块绑的是"图实例工厂"而非普通对象（M1 的 `BindFactory`）。

**影响**：`module-contract.md` M6 的表述按此改；`module-io-spec.md` §5.1 的 `session.actor` 行不变。

---

### D2 · 实例图不嵌套

**定论**：**先只允许一层**。实例图不能再引用另一张类型图。

**理由**：嵌套会让「有效图 = 类型图 ⊕ 实例图 overlay」变成递归合成，校验（U4 的集合包含、U5 的 Sealed 顺序）都要递归。而当前的嵌套深度已经够用：

```
graph.root ─ session.actor#42 ─ step#42 ─ {context, mind, vision}
```

**约束**：
- 加载期断言：`Composite.ClassId` 指向的类型图**不得**再含 `Composite` 模块。违反 → `GraphLoadException`。
- 如果将来要嵌第二层，必须同时补「递归合成 + 递归校验」两个测试，否则不许放开。

**代价**：未来要嵌得改校验器。可接受——现在没有第三个真实需求。

---

### D3 · overlay 不落结构，只落变量

**定论**：`SessionStateSnapshot` 保持**扁平字段**，**不**存节点/连线增量。实例图的结构部分永远由「类型图 + 该实例的构造参数」重建。

**理由**：
- 现有 `SessionStateSnapshot` 的 14 个字段（`RoundCount`/`WaitUntil`/`CompressionTier`/`ChannelCursors`/`ChildSessionIds`…）恰好是「变量值」而非「结构」。
- 存结构增量会让 `StateFormatVersion`（当前 2）必须每次结构改动都升版，且升级路径要处理「半旧半新结构」。

**约束**：
- 实例图的**结构**只能由 `CreateSessionSpec` 决定，且创建后不可变。
- `Snapshot/Restore` 只搬变量；`Restore` 时若类型图已 `Reloaded` 过，变量按名字对齐，缺失的取默认值、多余的丢弃并记 warning。

**影响**：`module-contract.md` Q18 关闭；`ISnapshotPort` 的 payload 是扁平字典，不是图结构。

---

### D4 · `swapClass` 等拍结束

**定论**：热换**排在当前拍之后**执行，不打断、不拒绝。

**理由**：`GraphInstance.Run` 的语义是「一次激活 = 一次求解，失败零副作用」。中途换类型图会让「本次求解用的是哪张图」变得不可判定，直接破坏原子性。

**约束**：
- `graph` 模块需要 `IsBusy`（只读数据口）与 `pendingSwap`（只读）。
- `swapClass` 的语义是**排队**，返回 `SwapAccepted(position: n)` 而不是 `SwapDone`。要观测完成，用 `Reloaded` 生命周期事件。
- 同一实例上多次 `swapClass` 只保留最后一次（后者覆盖前者，并记一条 warn）。

**代价**：要加「当前拍完成」信号。`IsBusy` 与 `lastResult` 已经存在，成本低。

---

### D5 · 拍前装载（这是唯一会改行为的决策）

**定论**：`channel.context` 的 IO **移出拍内**，改成**拍前装载**。引入显式的装载阶段：

```
装载（IO，可以 await）→ 冻结快照 → 求解（纯函数）→ 汇执行（IO）
```

**现状为什么是问题**：`RuntimeSession.cs:1032` 的 `await _host.BuildRoundAsync(channelDrained)` 就发生在拍内，它要查频道历史、参与者、滞后图片。同一段里还有 `AdvanceChannelCursor`。也就是说**现在的"拍"里混着 IO**。

**理由**：一旦允许拍内 IO，`Evaluate` 就不是纯函数，`Solver` 的「失败零副作用」也就只能保证「不写变量」，不能保证「没有半截读取」。而 `link-design.md` 的核心取舍「拍内一律 Inline」正建立在这个前提上。

**约束**：
- 装载产出 `LoadedBeat`（消息批次 + 频道上下文片段 + 轴快照 + 计划树），**一次性交进拍**。
- 拍内只允许纯计算；任何 `await` 都说明实现错了。可在 `Solver` 加调试期断言（`Evaluate` 期间禁止 `Task` 返回）。
- 引入新节点类型 `source.load`（装载源）与新的时序：`source.load → beat.boundary → solve → sink`。

**代价**：**这是行为改动**，`BuildRoundAsync` 的调用点要从拍内搬到拍前。但不做的话，「拍内纯求解」就是假的——而整个 link 模型建立在它上面。

**连带**：`module-io-spec.md` §5.3 的阶段 3 与 5（两次排空）要重划归属：它们从 `step` 的阶段变成**装载阶段的输入**。

---

### D6 · 亲和度统一到 `IChannelStore`

**定论**：新建 `IChannelStore` 端口（在 `contracts`），亲和度、配置、备注、占用**全部**经它读写。**只保留一个实现**（DB 或 json 二选一，由配置决定），不做双写。

**现状**（已验证）：
- `Database/Channel.cs:12` `Affinity`（默认 1.0）
- `ChannelStateManager.LoadConfig(channelId, dbAffinity)` 读 `ChannelState/{id}.json`，缺失时用 DB 值 → **json 优先、DB 兜底**
- 唯一调用点：`WebUI/Providers/ChannelProvider.cs:211`
- `ChannelContextPersistence` 存的是 `summary/mode/rounds/cursor/escalateReason`，**不含 affinity**

**理由**：两个存储 + 兜底顺序 = 迟早分叉。而且兜底只在一个读点生效，写入路径不明确——这是「看起来能用，改起来会咬人」的典型。

**约束**：
- `channel.state` 只经 `IChannelStore` 读写；`ChannelStateManager` 的 static 形态取消（同时满足 R5）。
- 迁移：读一次 DB → 若 json 存在且值不同 → 记一条 `channel.affinity.conflict` 事件后**以 json 为准**（保持现状语义），然后写回 DB，之后只走 store。
- 兜底顺序写进 `IChannelStore` 契约注释，不允许调用方自己再判。

**影响**：`node_modules` 里 `channel.occupancy`（Singleton）与 `channel.state`（PerChannel）都改经 store。

---

### D7 · `mind` 拆 5 域 + 壳并入 host

**定论**：

| 模块 | 组成 | 行数 |
|---|---|---:|
| `mind.emotion` | `EmotionService`(192) + `EvidenceEngine`(188) + `EvidenceMath`(54) | 434 |
| `mind.write` | `Writer`(237) + `ConflictService`(128) | 365 |
| `mind.memory` | `RetrieveService`(351) + `Resurrector`(118) + `EventSink`(95) | 564 |
| `mind.review` | `ReviewStage`(245) + `ReviewQueueService`(106) | 351 |
| `mind.extract` | `ExpressExtractor`(385) + `ExtractService`(72) + `IExtractionLlm` | 457 |
| （壳）| `MindEngine`(132) + `SpawnCheck`(37) → **并入 host** | 169 |

**理由**：`ConflictService` 的注释自述「由 `Writer` / `MindEngine` 持有使用」，独立出去会出现两处引用它；而它是 `Writer` 落库时的冲突判定，归 `write` 是内聚的。

**约束**：3 条内部依赖必须端口化，否则 5 域会互相回指：

| 依赖 | 现状 | 改法 |
|---|---|---|
| `mind.write` → `mind.emotion` | `Writer` 构造注入 `EmotionService` | 改经 `IEmotionSignalPort`（写情绪信号，不读情绪状态） |
| `mind.write` → 冲突判定 | 同文件，已内聚 | 不变 |
| `mind.memory` → `mind.write` | `Resurrector` 持 `MindEngine` 取 `Writer` | 改经 `IWriterPort`（已有 `IWriterAccess` 可复用） |

`ExpressExtractor` 已经依赖 `IWriterAccess` 接口 —— **这是全项目做得最对的一处**，其余照它办。

---

### D8 · `Stable` 保持作者契约

**定论**：不加强制验证。`PromptContribution.Stable` 继续是「插件作者承诺」，宿主不做哈希比对。

**理由**：ALC 的实现注释明确：「稳定性目前是插件作者契约，宿主不通过执行隔离证明其正文不会变化；需要随档位变化的内容应放在 Floating」。加验证要定义「会话内」的边界（跨拍？跨切核？跨恢复？），收益不抵复杂度。

**约束**：
- 保持现有规则：`System`/`Primary` 必须 `Stable=true`，`Floating` 必须 `false`（违者注册期抛）。
- 文档里要写清「不验证」这件事，避免后来者以为是 bug。

---

## 2. 由决策派生的新增约束（可直接当校验规则）

| # | 断言 | 来源 |
|---|---|---|
| **U10** | `Composite.ClassId` 指向的类型图不得再含 `Composite` 模块 | D2 |
| **U11** | 实例图 overlay 只含变量值；出现 `nodes`/`links` 键即拒绝 | D3 |
| **U12** | `swapClass` 必须返回「排队」而非「完成」；同一实例多次调用只保留最后一次 | D4 |
| **U13** | `Solver` 求解期间不得出现未完成的 `Task`（调试期断言） | D5 |
| **U14** | 亲和度只能经 `IChannelStore` 读写；调用方不得自带兜底顺序 | D6 |
| **U15** | `mind.write` 不得持有 `EmotionService` 具体类型，只经 `IEmotionSignalPort` | D7 |
| **U16** | `mind.memory` 不得持有 `MindEngine` 具体类型，只经 `IWriterPort` | D7 |

---

## 3. 对已有文档的回改清单

| 文档 | 要改什么 |
|---|---|
| `module-contract.md` | M6「复合模块」按 D1 改写（新增 `[Composite]` 而非挪 `[Module]`）；Q12/Q17/Q18/Q20 标记关闭 |
| `module-catalog.md` | `mind` 6 域 → 5 域 + 壳（D7）；overlay 持久化按 D3 |
| `link-design.md` | §8 执行语义增加「装载阶段」（D5）；这是唯一的行为性改动 |
| `module-io-spec.md` | §5.3 阶段 3/5 从 `step` 的「阶段」改为「装载输入」（D5）；§6 `mind` 5 域（D7） |
| `session-decomposition.md` | §5.1 的 `ISnapshotPort` payload 改为扁平字典（D3） |
| `verification-report.md` | 8 项决策关闭，指向本文 |

---

## 4. 生效后的执行顺序（与计划一致）

```
A1  axes 模块（轴表 + 冻结快照）          ← 先做，不依赖上面的决策
A2  PromptAssembly 契约
A3  context 四件事分开
A4  step 边界（12 阶段 + H1/H2/H3）
B1  拆 session 文件（12 + 8 部件）
B2  5 条新端口 → 改为「1 个纯函数解释器 + apply 出口」
B3  ISnapshotPort（按 D3 的扁平 payload）
C   拆 csproj
D   影子图对拍（含 D5 的装载阶段验证）
```

**D5 是唯一会改行为的决策**，它的验证点在 D（影子图对拍）：如果拍前装载之后对拍不过，说明现有拍内 IO 有不可前移的时序依赖——那时要回头重审 D5。
