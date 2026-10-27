# 迁移影响：AgentLilaraCode 的浮动上下文机制 vs 当前模块切分

> 检查对象：`E:\Workspace\AgentLilaraCode`（221 个 .cs / 53648 行，5 个项目）
> 结论：该机制暴露出当前切分的 **6 处缺口**，其中 2 处要新增模块、1 处要拆模块。
> 未实现、未编译。本文只做影响分析，不改树 —— 树怎么改见 §5。

---

## 1. 那套机制由什么组成

| 组件 | 位置 | 行数 | 职责 | 现状标记 |
|---|---|---:|---|---|
| `Axes` | `Kernel/Axes.cs` | 442 | 核心轴的**唯一真相表**：轴名/中文名/合法值/默认值/`InContext`/等级解释全在一张 `Table` 里；`Names`（人看的序）与 `RenderOrder`（ordinal 序）分开；`TryParse` 负责规范化；数值轴（temperature）用网格生成 `Values` | 完整 |
| `IAxisPlugin` / `AxisDefinition` | `Plugins/` + `Kernel/` | — | 插件按 `plugin-id.axis-id` 命名空间注册轴，schema 原子校验 | 完整 |
| `PluginHost` | `Plugins/PluginHost.cs` | ~324（目录） | `Register`（命名冲突/容量/能力校验）、`Freeze`（每步冻结轴快照）、`Collect`（按 `(pluginId,key)` ordinal 排序合并，自动生成 `[pluginId/axis-level.<id>]` 字段） | 完整 |
| `PromptContribution` + `PromptSlot` | `Plugins/PromptContribution.cs` | 40 | 插件**只贡献字段**（PluginId/Key/Slot/Text/Stable），宿主负责排序拼接 | 完整 |
| `SystemBlock` → `PromptSlots` | `Session/SystemBlock.cs` | 242 | 三槽位装配；`Render`（读盘）与 `Build`（纯装配）分离——答复路径与控制面走 `Build` | 完整 |
| `FloatingContextTool` | `Session/Tools/` | 133 | 每步必调的**记账工具**，返回占位；`Framing` / `ReclaimedFraming` 常量 | 完整 |
| `Stepper` | `Session/Stepper.cs` | **1677** | 一步的推进：`FloatingContextPair`（每步合成一对 `tool_use`+`tool_result` 插在时间线**最前面**）、`SealFloatingCall`（记录里每步留占位）、终止判定、计划、goal loop | 完整 |
| `Reclaim` | `Session/Reclaim.cs` | 396 | 工具结果回收；`IsFloatingPlaceholder` 保证占位不被回收 | 完整 |
| `TimelineRender` | `Session/TimelineRender.cs` | 369 | 时间线渲染；`RenderForModel` / `WithFraming` | 完整 |
| 内建工具 | `Session/Tools/` | **~6000**（30 文件） | dotnet/shell/git/read/write/plan/ask/spawn/tell/compact/release… | 完整 |

### 三条硬约束（迁移时必须原样搬过来）

1. **每步冻结一次快照**：轴值、插件贡献、计划树、子会话行在"步开始"取一次并冻住。理由是渲染必须是纯函数——同一步内重复渲染要**逐字节相同**，否则每步都破提示词前缀（缓存全废）。所以 `Render` 与 `Build` 必须分开：答复路径拿不到快照时用 `Build`，不能去读盘。
2. **顺序是契约，不是实现细节**：三槽位顺序固定（System → Premier → Tail）；插件字段按 `(pluginId,key)` 的 **ordinal** 排序；轴表按 `RenderOrder`（ordinal，不是 `Names` 的表序）。理由写在代码注释里：文化序（ICU）会让 `i`/`I` 在 `en-US` 与 `tr-TR` 下序不同，同一份数据两台机器两个顺序，**静默破前缀**。
3. **占位是机械保证，不靠文案**："读数在对话最前面"这件事靠 `FloatingContextTool` 返回一句固定占位来保证，不是靠工具描述里劝模型别调。历史上正是因为用了"劝"，模型一步内连调十余次。

---

## 2. 你的切分现在缺什么（6 处）

### A. 缺 `axes` 模块 —— 最明确的一处

当前 `modules.json` 里**没有"轴"这个模块**。但它被三方共享：

| 谁用 | 用来干什么 |
|---|---|
| `context` | 渲染尾块：轴表 + 当前等级解释（`Axes.InContext` 决定哪些进） |
| `turn` / 单次模型调用 | `effort`、`temperature` —— `InContext:false`，只进请求参数 |
| `webui` / `commands` | 控制面 `/axes`、下拉、跨轴告警 |

按 §0 的唯一性规则：它是静态表 + 冻结快照，**无私有状态** → **Singleton**。
放 `contracts` 不行（442 行逻辑 + 数据，违反"零逻辑"）；塞进 `context` 不行（webui 独立进程后拿轴值要穿两层）。

→ **新增 `axes`（唯一）**：行为 `valuesOf` / `labelOf` / `tryParse` / `descriptionOf` / `numberOrNull` / `defaults` / `freeze`；数据 `names`、`renderOrder`、`numericAxes`；生命周期 `Mounted`（加载核心表 + 插件轴）/ `Sealed`（注册封闭）。

### B. 缺"内建工具"归属

我只有 `plugin-host` 管工具，但 `floating_context` / `plan_*` / `spawn` / `tell_child` / `finish_goal` / `compact` / `release` / `ask` 全是**内建工具**——编译进 host，不走 ALC。AgentLilaraCode 那 30 个文件 ~6000 行就是这块。

→ **新增 `builtin-tools`（唯一）**，与 `plugin-host` 并列：前者是编译期工具，后者是运行期插件。两者对 `turn` 暴露同一个 `IToolHost` 端口，`turn` 不需要知道工具从哪来。

### C. `turn` 的粒度错了

我写的是「`turn`：单轮执行：组上下文 → 调模型 → 执行工具 → 落库」。但 AgentLilaraCode 的 `Stepper`（1677 行）说明**一拍之内会有 N 次模型调用**（工具循环），而"一步"和"一次模型调用"是两层：

```
step（推进器，一拍）
  ├── 组上下文（一次，冻结）
  ├── turn#1（一次模型调用）→ 工具调用 → 结果
  ├── turn#2 → …
  └── 终止判定 / 落库 / 产出 intent
```

→ **拆 `step` 与 `turn`**：`step` 是 PerSession 实例（一拍一个），`turn` 是 step 内部的一次调用（可以不作为对外模块暴露，只是 step 的私有实现；但若要观测 token/耗时，就值得独立）。**我倾向 `turn` 不作为模块**，退化为 `step` 内部结构 —— 否则模块数量虚增，且它没有独立生命周期。

这条同时解释了之前 Q15/Q16 的困惑：`OnBeforeInvoke` 是**每次模型调用**，`Activated/Deactivated` 是**一拍**。有了 step/turn 分层，两者各归其位。

### D. `context` 的职责写漏了两块

我写的是「三层压缩 / 注入 / 提示贡献汇总」。实际有四件事，**判据各不相同**：

| 事 | 粒度 | 判据 |
|---|---|---|
| 三槽位装配（System/Premier/Tail） | 每步一次快照 | 文件在不在 / 空不空（`null` 与 `""` 是两回事） |
| 插件贡献合并 | 每步一次快照 | `(slot, pluginId, key)` 去重 + 容量上限 |
| **工具结果回收**（`Reclaim`） | 结果级 | 单条结果是否可回收；**占位永不回收** |
| 层压缩（L1/L2/L3） | 会话级 | 上下文用量越线 |

"结果回收"和"层压缩"是**两种不同机制**，我原来混成一句"三层压缩"。迁移时如果按我原来的划分落，这两块会挤在一个类里，重演 `ChannelContextService` 那种"什么都干"。

### E. 顺序在图的模型里没有位置 —— **这是最硬的缺口**

浮动块必须插在**对话最前面**；三槽位顺序固定；插件字段 ordinal 排序。这三条都是**顺序契约**。

但图的 `Link` 只表达"谁给谁数据"，**不表达输出的排列顺序**。如果 `context.assemble` 的输出只是一个 `Cell{payload: object}`，那么节点求值顺序一变，提示词字节就变——而"同一步逐字节相同"是硬约束。

→ 必须把顺序**提升到端口的数据类型里**：

```csharp
public sealed record PromptBlock(
    PromptSlot Slot,        // System | Premier | Floating
    string Owner,           // "core" | "plugin:<id>"
    string Key,
    string Text,
    bool Stable,            // System/Primary 必须 true，Floating 必须 false
    int Order);             // 槽内 ordinal 序

public sealed record PromptAssembly(
    IReadOnlyList<PromptBlock> Leading,  // 浮动上下文对：插在对话最前面
    IReadOnlyList<PromptBlock> Slots);   // System → Premier → Tail
```

`Order` 与 `Leading/Slots` 的分组是**契约**，不是实现细节。这样"顺序"可断言、可测试，不依赖连线顺序。

### F. 计划树与子会话视图没进目录

`SessionPlan` + `PlanTree.Render`（`Kernel/Plan.cs` 296 行）、`SubagentView`（63 行）都进尾块。我的 `module-catalog.md` 完全没提计划树。

→ 判为 **`session` 的私有状态 + `context` 的渲染输入**，不独立成模块（生命周期绑会话，无跨会话语义）。但要在 `session.actor` 的数据里补 `plan`（读写）、`subagents`（只读），在 `context` 的行为里补 `renderPlan`。

---

## 3. 迁移后的目标形状（只列变化部分）

```
host
├── axes                    ← 新增（唯一）：轴真相表 + 插件轴注册 + 每步冻结
├── builtin-tools           ← 新增（唯一）：编译进 host 的工具
├── plugin-host             ← 保留：ALC 插件（含插件轴贡献）
├── context                 ← 职责细化为四件事 + 输出 PromptAssembly
└── session
    ├── step                ← 原 turn 改名/降级：一拍（含 N 次模型调用）
    │   ├── context
    │   ├── mind
    │   └── vision
    └── recurring
```

`axes` 与 `plugin-host` 的关系：**插件轴由 plugin-host 注册进 axes**，`axes` 不反向依赖 `plugin-host` —— 注册是 `plugin-host` 调 `axes.register(...)`，方向单向。

---

## 4. 迁移切口（按依赖排出）

| # | 切口 | 为什么在这个位置 | 风险 |
|---|---|---|---|
| F1 | `axes` 模块独立（表 + 冻结快照 + 插件轴注册） | 其他全部依赖它；没有它，尾块与请求参数都无处来 | 低 |
| F2 | `PromptSlot` / `PromptContribution` / `PromptAssembly` 进 `contracts` | 插件契约；必须在 `plugin-host` 之前定 | 低 |
| F3 | `context` 四件事分开（装配 / 合并 / 回收 / 压缩） | 顺序契约落在 `PromptAssembly` 上 | 中 |
| F4 | `builtin-tools` 独立 | 与 F5 一起动 `step` 的工具调用点 | 低 |
| F5 | `turn` → `step`（并明确 turn 不暴露为模块） | 一拍内 N 次模型调用这个事实必须先承认 | 中 |
| F6 | 浮动占位对（`FloatingContextPair` + 占位工具 + 回收保护） | 依赖 F3 的 `Leading` 概念 | 中 |
| F7 | 计划树 / 子会话视图接进尾块 | 纯增量，可最后做 | 低 |

**F1 + F2 可以先做**，它们不碰 `step`，也不碰图。

---

## 5. 要不要现在改树

`modules.json` 目前是 20 个模块。按 §2 要变成：

| 变化 | 前 | 后 |
|---|---|---|
| 新增 | — | `axes`、`builtin-tools` |
| 改名/降级 | `turn`（PerSession） | `step`（PerSession）；`turn` 不再是模块 |
| 装配边 | `session → turn`, `turn → {context, mind, vision}` | `session → step`, `step → {context, mind, vision}` |
| 端口边 | `turn → llm/data/plugin-host/logging/channel` | `step → …`；新增 `step → axes`（请求参数）、`context → axes`（尾块） |

净变化：**+2 个模块，`turn` 变 `step`，边需要重挂**。这会让装配树从 20 个节点变成 22 个，且 `step` 的子树整体上移一层。

**我没有直接改** —— 因为这会动到 `tree-cut.md` 的 14 个切口编号（`C2` 抽会话端口、`C13` webui 独立都引用了 `turn`），改一半更乱。要改就一次性改，你点头我再动。

---

## 6. 未验证

| id | 问题 |
|---|---|
| Q22 | `Stepper` 1677 行里，"一拍"还包含计划树推进与 goal loop 的终止判定。这些属于 `step` 还是 `session`？我只读了文件规模和 `SystemBlock` 里的调用点，没读 `Stepper` 正文 |
| Q23 | `Reclaim`（结果回收）与 `ContextCompaction`（100 行）是两层，还是后者是前者的入口？两者都只在 `Session/` 下，未读 |
| Q24 | AgentLilaraCode 的"协议三构造器"（Claude Messages / OpenAI Chat / OpenAI Responses）各自消费 `PromptSlots`。迁到本项目后，`llm` 模块是否要承担同样的三协议装配？现有 `Client/` 18 文件 2677 行里已有多家客户端 |
| Q25 | 插件的 `Stable` 约束（System/Primary 必须 true，Floating 必须 false）是**作者契约**，宿主不验证正文稳定性。迁过来要不要加强成可验证？ |

**未验证声明**：以上全部基于对 `E:\Workspace\AgentLilaraCode` 的静态阅读（`Axes.cs` / `SystemBlock.cs` / `FloatingContextTool.cs` / `PromptContribution.cs` / `PluginHost.cs` 相关段 + `docs/07-轴插件接入.md`）。**未编译、未运行该仓库**，也未读 `Stepper.cs` 全文。`Stepper` 的"一拍"具体边界（Q22）是本文最大的不确定处。
