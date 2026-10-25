# 节点目录（05）

> 节点 = **模块接口 / 动作在画布上的投影**（`00-decisions.md` D56）。它们不该手写，而应从清单导出：
> 动作节点来自 `IntentCatalog`（`15-wiring-contract.md` §4），模块节点来自 `ModuleCatalog`（`14-modules.md` §3）。
> 状态：**与实现同步**（`src/AgentLilara.Kernel/Nodes/`）。

---

## 1. 命名与分类

| 前缀 | 含义 | 对应对象模型 |
|---|---|---|
`on.*` | **激活源**：生命周期入口，每图**恰好一个** | 生命周期 |
`read.*` | **数据源**：常量 / 变量（本拍快照） | 属性（读） |
`event.*` | **算子**：读当前事件的字段、判断是否点名 | 表达式 |
`logic.*` / `compare.*` / `math.*` / `list.*` | **算子**：内建纯函数，无状态无副作用 | 表达式 |
`sink.*` | **激活汇**：产出动作意图（不执行） | 方法 |
`write.*` | **数据汇**：写实例变量 | 属性（写） |
`module.<模块>.<方法>` | 模块导出的方法节点（由清单生成） | 方法 |
`read.<模块>.<属性>` / `write.<模块>.<属性>` | 模块导出的属性节点 | 属性 |

**没有"拼接块"这一类**：跨拍行为由"动态图 + 变量"表达；等待由动作语义表达；边沿由实例的使能语义表达。

---

## 2. 激活源（每图恰好一个）

| 节点 | 参数 | 输出 | 备注 |
|---|---|---|---|
`on.message` | — | `event`（`InboundEvent`）、`channel` | **已实现**；本拍事件为空时输出无事件 |
`on.clock` | — | `event` | **已实现**；动态图专用（时钟沿），归档保留 |

> 一图一源（D65）。要"多个条件都走同一反应"，用中间层图 + `sink.pulse`，不要放开多源。

---

## 3. 数据源

| 节点 | 参数 | 输出 |
|---|---|---|
`read.const` | `value`（bool/int/string） | `value` |
`read.variable` | `name` | `value`（本拍快照） |
`read.<module>.<property>` | — | `value`（模块属性，读上次提交值） |

---

## 4. 算子（闭合集，只增不改）

| 节点 | 输入 | 输出 |
|---|---|---|
`event.get` | `event` | `value`（按 `field` 取；字段见下） |
`event.mention` | `event` | `result`（`Bool`；上游无事件 → 无事件） |
`logic.and` / `logic.or` | `a`、`b` | `result`（`Bool`） |
`logic.select` | `cond`、`a`、`b` | `result`（`cond ? a : b`，只选择、不惰性求值） |
`gate.if` | `event`、`cond` | `event`（条件不成立 → **无事件**） |
`compare.ge` | `value`、`threshold` | `result`（`Bool`） |
`math.add` | `a`、`b` | `result`（`Int`） |
`list.first` | `list` | `value`、`found`（空列表 → `value` 无事件） |

`event.get` 的 `field` 白名单（拼错在加载期报错）：

```
text  sender  channel  adapter  kind  mentions  isReply  received  dedupe
```

**刻意不给**：`filter` / `map` / `reduce` / 用户自定义函数 / 字符串处理全家桶（D62）。需要这些就写进模块。

---

## 5. 动作汇块（激活汇）

动作汇块**只产意图**，执行归骨架（D63）。节点类型 ↔ 动作的映射在 `IntentCatalog` 里，加动作 = 加一行。

| 节点 | 输入（除 `event`） | 产出意图 | Target 来源 |
|---|---|---|---|
`sink.send` | `text` | `SendMessage` | 事件频道 |
`sink.prompt` | `sessionRef`、`text` | `SessionPrompt` | `sessionRef` |
`sink.inject` | `sessionRef`、`text` | `SessionInject` | `sessionRef` |
`sink.set` | `key`、`value` | `StateSet` | `key` |
`module.<id>.<method>` | `event` + 每个参数一个口 | 即时调用（结果进台账） | — |

**图上没有节点的动作**（骨架内部，问题不在于"权限"而在于"必须发生"）：落库、去重、出向台账与重试、真正发送、会话持久化、审计。

---

## 6. 数据汇

| 节点 | 参数 | 说明 |
|---|---|---|
`write.variable` | `name` | 写实例变量（激活成功结束才提交） |
`write.<module>.<property>` | — | 写模块属性（阶段③执行） |

---

## 7. 插口约定

| 项 | 规则 |
|---|---|
事件口 | `event` 携带 `InboundEvent`；未触发的源不产出事件 |
值口 | `Bool` / `Int` / `Text` / `List` / `record`；必须声明常量默认值 |
方法口 | 参数即实参；`CancellationToken` 不占端口 |
汇块触发口 | 一律叫 `event`：已知 = 使能，无事件 = 本拍不触发 |
汇块输出 | **无输出口、无 `fired`**；返回值进 `SinkOutcome.Outputs` 台账 |
汇块副作用 | 图内零副作用；派发动作产意图，即时动作在阶段③执行 |

---

## 8. 校验摘要

见 `04-graph-model.md` §8。节点侧额外要求：

- `event.get` 的 `field` 必须在白名单里（加载期报错）。
- 模块节点必须能在 `ModuleCatalog` 里找到类型，且模块已 `Bind` 实现。
- 未知参数一律报错（防错拼被静默忽略）。

---

## 9. 尚未实现

| 项 | 说明 |
|---|---|
`on.signal.*` / `on.timer` / `on.lifecycle` / `on.resource.dropped` | 已定契约，未实现 |
`sink.pulse` | 未实现（`preset.digest` 的前置条件） |
`sink.publish` / `sink.emitSignal` / `sink.cancelKill` | 已定契约，未实现 |
`wire.*`（describe/validate/apply） | 未实现（L-B 后置） |
`waitMs` 与资源队列 | 未实现 |
端口静态类型 | 端口仍是字符串名 + `Cell(object)`，需升级为带类型声明 |
