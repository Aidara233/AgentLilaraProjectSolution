# 接线与模板（10）

> 图只表达**模块之间**的连线（`00-decisions.md` D56）。节点目录见 `05-nodes.md`，执行语义见 `04-graph-model.md`，模块契约见 `14-modules.md`。
> 本文回答：怎么连、有哪些内置模板、新建时默认接什么。

---

## 0. 不接线也能工作

| 行为 | 需要画图吗 |
|---|---|
消息入库、归档、索引、按平台查询 | **不需要**（Ingest 无条件做） |
命令 `/xxx` | **不需要，也不该**图化（Gate 阶段拦截） |
记忆提取、会话收尾摘要 | **不需要**（内核固有流程，D44） |
发起会话、回应、通知、巡检 | **需要**（方法节点是唯一出口） |

---

## 1. 接线合法性

| 规则 | 说明 |
|---|---|
事件边 | `Event/Message/Signal` → `Event`；不得接值口 |
值边 | 同类型直连；`Message→Event`、`Signal→Event` 允许向上转换 |
汇块 | 必须终结；不得有下游；无 `fired` |
激活源 | 每图**恰好一个**；想看多个入口 → 拆成多张图，各自调用同一个方法（`11` §3） |
模块边界 | **禁止图套图**：模块内部时序不可在画布上展开 |
规模 | 单图节点数预期 ≤ 20–30；超过说明模块切分过细 |
作用域 | 状态只在"变量/模块内部"里；跨模块读取一律读**上次提交值** |
一次 tick | 同一图内恰好一个激活源触发 |

---

## 2. 模板：内置 / 自定互相独立

| 类别 | 存放 | 可编辑 | 升级 |
|---|---|---|---|
内置模板 | `Storage/Templates/builtin/{id}.json`（随版本发布，只读） | **不可就地编辑** | 随版本更新；引用记 `builtin:{id}@{version}` |
自定模板 | `Storage/Templates/custom/{id}.json`（可写） | 可编辑、可删除 | 与内置完全独立 |

- 从内置"复制一份"→ `custom:{id}@{n}`，带 `basedOn` 仅作溯源。
- 自定模板可嵌套（深度 ≤ 3，禁自引用）。
- 模板是**图**，不是块的组合：它本身就遵循"一图一激活源"。

---

## 3. 内置模板清单

### 3.1 静态图模板（一次激活跑一次）

| 模板 | 接线 | 用途 |
|---|---|---|
`T1 点名响应` | `on.message` → `event.mention` → `gate.if` → `module.agent.loop.run` → `sink.send` | 被叫到才回 |
`T2 关键词响应` | `on.message` → `compare.matches` → `gate.if` → `T1` 后半 | 关键词触发 |
`T3 静默时段`（子图） | `read.channel` / `read.time` → `compare.in` → `logic.not` | 作为 `and` 的一项插入任意链 |
`T6 等资源` | `module.agent.loop.run(waitMs = N)` | 忙时排队或立即放弃（`0`） |
`T9 邮件分拣` | `on.email` → `read.email` → `compare.in(from)` → `module.agent.loop.run` → `sink.respond` | 每封邮件独立事件 |
`T10 传感越界` | `on.ha` → `read.ha` → `compare.lte/gte` 两分支 → `module.agent.loop.run` + `sink.send` | 过冷/过热不同提示词 |
`T11 定时汇报` | `on.timer(cron)` → `module.agent.loop.run` | 日报/巡检 |

### 3.2 动态图模板（时钟驱动，持有变量）

| 模板 | 变量 | 对外 | 用途 |
|---|---|---|---|
`D1 节流` | `lastAt` | 属性 `allowed`、方法 `touch()` | 同频道 N 秒内只放一次 |
`D2 去抖` | `lastAt` | 属性 `pending`、方法 `feed()` | 窗口内无新输入才放行 |
`D3 计数` | `count` | 属性 `count`、方法 `inc()` / `reset()` | 攒够 N 次再动作 |
`D4 锁存` | `flag` | 属性 `on`、方法 `set()` / `clear()` | 跨激活记住"曾经发生过" |
`D5 边沿` | `prev` | 属性 `rising` / `falling` | Bool 的上升/下降沿 |

**组合方式**（静态图怎么用动态图）：

```
静态图:  on.message → read.module("D1@channel:X").allowed → gate.if → module.agent.loop.run
                                                         ↘ sink.pulse("D1@channel:X.touch")
动态图 D1: on.clock → ...更新 lastAt...
```

即：**状态放在动态图里，判定读它的属性，触发后给它脉冲**。这样图上没有"隐藏状态"。

### 3.3 信号驱动模板

| 模板 | 接线 | 用途 |
|---|---|---|
`T8 适配器排障` | `on.signal.adapter.down` → `module.agent.loop.run(task)` + `sink.emitSignal(notify)` | 宕机自动排查 |
`T8b 恢复通知` | `on.signal.adapter.up` → `sink.emitSignal(notify)` | 恢复也发 |
`T7 丢弃攒批` | `on.resource.dropped` → 动态图 `D3 计数.inc()`；`on.timer(60s)` → 读 `D3.count >= 3` → `module.agent.loop.run` | 独占期间消息不静默丢失 |
`T12 会话收尾` | `on.signal.session.finished` → 可选 `sink.emitSignal(digest)` | 内核已处理摘要，图只做增量 |

---

## 4. 新建时不接线

| 场景 | 行为 |
|---|---|
attach 频道 | 只登记绑定、开始入库。**不生成任何图**；WebUI 提供"套用模板"入口（推荐 `T1`，需确认） |
新建邮件源 | 只开始收信入库；提供 `T9` 模板 |
新建 HA 源 | 只接收实体状态；提供 `T10`，阈值必须由用户填 |
新建 webhook 源 | 只接收请求；提供最小模板 |
任意源 | 可选挂 `T7`（默认不挂，避免噪声） |

唯一自动的是**归档与索引**，不涉及任何响应行为。

---

## 5. 反模式

| 反模式 | 为什么 | 建议 |
|---|---|---|
把 `compare.matches` 写成巨型正则 | 难调试、回溯风险 | 拆成多个条件 + `logic.or` |
用随机节点做概率决策 | 非确定，重放不一致 | 标 `Nondeterministic`，或用 `D3 计数` 节律 |
先 `waitMs` 再判断 | 白占排队名额 | 先判断后调用 |
在图上展开某个模块的内部 | 违反模块边界，图会失控 | 改代码拆分模块 |
多入口塞进一张图 | 违反"一图一激活源" | 拆图 + 共享方法 |
静态图里按频道存状态 | 静态图只有一个实例 | 用该频道的动态图实例 |

---

## 6. 实施顺序

| 批次 | 内容 | 验收 |
|---|---|---|
P0 | `on.message`、`event.get`、`event.mention`、`logic.and`、`gate.if`、`compare.ge`、`sink.prompt`+ `T1` | 手工搭出"@才回"，沙箱跑通 |
P1 | `module.agent.loop.run`、`sink.send`、`sink.emitSignal`、`on.signal.*`、`on.timer`、`D1/D3` 动态图 | 真实消息驱动真实会话 |
P2 | `on.lifecycle`、`sink.pulse`、`sink.publish`、`read.module.*`、`waitMs` | 生命周期与跨图协作 |
