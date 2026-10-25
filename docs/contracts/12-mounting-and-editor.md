# 挂载、实例与"图即代码"（12）

> 依据：`11-object-model.md`（类/方法/属性/变量）、`04-graph-model.md`（求解与时钟）。
> 本文回答：图挂在哪、实例怎么生灭、变量怎么暴露，以及承认"这就是个可视化代码编辑器"之后必须补的能力。

---

## 0. 定位

**图 = 源码，执行器 = 运行时，WebUI = IDE。**

由此推出四件事，缺一不可：源码（可 diff/可版本化）、编译（校验）、实例（状态与生命周期）、调试（trace / 变量 / 试跑）。

图不是配置数据，是**资产**：可以导出、导入、复用、评审。

---

## 1. 三个层次：类 / 挂载 / 实例

| 层 | 内容 | 例子 |
|---|---|---|
**类（GraphClass）** | `{classId, kind: Static\|Dynamic, version, hash, sourcePath, params}` | `channel.responder`、`channel.throttle` |
**挂载（Mount）** | `{classId, scope, instanceName, params}` —— "这个类在哪个作用域生效" | `channel.throttle @ Channel` |

`params` 是该挂载点提供给图读取的只读键值参数；例如 `preset.router` 可在频道挂载的 `params.sessionType` 指定空闲频道要创建的会话类型。图通过 `read.mount` 节点读取，未配置的参数为 unknown。
**实例（GraphInstance）** | `instanceKey = {classId}@{scope}#{instanceName}`，持有变量与上次提交快照 | `channel.throttle@Channel:616565017#` |

### 作用域

```
Global           进程级（唯一）
ChannelManager   频道管理器自身
Channel(channelId)  单个频道
Session(sessionId)  单个会话
Plugin(pluginId)    插件自带
```

### 实例化规则

| 类 | 实例数 | 说明 |
|---|---|---|
**静态类** | **唯一**（每进程一个） | 挂载只表示"在某作用域启用"，不产生新实例 |
**动态类** | **每个 scope 键一个** | 同一 scope 可挂多个类，各自独立 |

所以"给频道设计图"不是"给频道画一张图"，而是**定义类 + 在 Channel 作用域挂载**；频道数量变化不改变类，只改变实例数量。

---

## 2. 本拍上下文（`this`）

静态类被所有频道共享，因此激活源必须注入上下文：

```
tick.channel    本次激活所属频道（静态图中即"当前 this"）
tick.session    所属会话（若来自会话）
tick.event      触发它的入站事件引用
tick.traceId / tick.segment
```

数据源与激活汇可**省略目标参数**，默认取 `tick.channel` / `tick.session`。这既保持"共享一个静态类"成立，也避免每处都要显式写频道。

**关键限制**：静态类**没有按频道分区存储的能力**（它只有一个实例、一份变量）。凡"记住每个频道各自的上一拍/计数/待办"的需求，必须落在该频道的**动态实例**里——这是静态类与动态类分工的硬边界。

---

## 3. 生命周期：与挂点共存亡

| 阶段 | 触发 | 行为 |
|---|---|---|
出生 | 挂点建立（频道 attach / 会话创建 / 首次激活） | 静态类：注册复用；动态类：按 scope 键实例化、注入变量初值，随后激活 `on.mount`（若挂了出生图） |
运行 | 时钟 / 事件 | 正常 tick |
死亡前 | 挂点消失（频道 detach、频道被删、会话结束） | **先激活一次 `on.kill`**：可读变量、可发信号、可调用 `sink.cancelKill()` **取消本次销毁** |
死亡 | 钩子未取消 / 强杀 | 实例销毁；变量默认**落盘留档**（便于事后排查），可按类配置为丢弃 |

**强杀**：`instance.kill(force: true)`，或取消次数超阈值 → **跳过 `on.kill`**，直接销毁。

### 与"一图一激活源"的衔接

"出生 / 死亡前"是**独立的小图**，不是给同一张图加第二个激活源：

```
channel.throttle        时钟图（on.clock）
channel.throttle.kill   销毁前图（on.kill）   ← 需要才挂
```

这样"每图恰一个激活源"不变，生命周期只是多挂一个类。

### 取消销毁的防僵尸规则

| 项 | 规则 |
|---|---|
取消语义 | `cancelKill()` 只否决**本次**销毁请求，不阻止未来请求（真被强杀时依然会死） |
次数上限 | 每次取消 `killCancelled++`；超过阈值（默认 3）后续销毁请求直接强杀 |
审计 | 写 `LifecycleAudit(instanceKey, reason, cancelCount, at)` |

---

## 3.1 挂载表与"预制件"覆盖

**挂载表是数据，可选**：某个作用域什么都不挂，就只入库不响应。

```sql
GraphMounts(Id, Scope, ScopeKey, ClassId, InstanceName, ParamsJson, Enabled, Order)
-- Scope: Global | ChannelManager | Channel | Session | Plugin
-- ScopeKey: 频道 id / 会话 id / 空（全局）
```

| 概念 | 对应 |
|---|---|
**类（GraphClass）** | Unity 的**预制件**：完整装配、默认参数、变量 schema 与初值 |
**挂载（GraphMount）** | 预制件在某个作用域的实例声明（可禁用、可排序） |
**实例覆盖（Override）** | 针对**单个频道**覆盖：参数值、变量初值、甚至换用变体类 |
**变体（Variant）** | 基于类的子类（继承端口与块，可覆盖参数与提示词），用于"某类频道行为不同" |

```sql
GraphOverrides(Id, MountId, ChannelId, ParamsJson, VariableInitJson, ReplaceWithClassId)
```

典型用法：所有频道挂 `channel.responder`；某个客户群单独挂变体 `channel.responder.strict`（更严的冷却、不同提示词），只覆盖一个频道，不影响其余。

---

## 4. 频道场景的标准布置

```
静态类  channel.responder        唯一实例      ← 所有频道的消息都激活它
动态类  channel.throttle        每频道一实例   ← 变量：lastAt / count
动态类  channel.digest          每频道一实例   ← 变量：待处理消息队列
动态类  manager.supervisor      ChannelManager ← 频道健康巡检、新增频道发现
动态类  session.budget          每会话一实例   ← 变量：本轮次/预算
```

规则：
- **"跨频道共享的规则"放静态类**（回不回、怎么回）；
- **"每对象独立的状态"放该作用域的动态类**（节流、攒批、计数）；
- **"管理一批对象的逻辑"放管理器的动态类**（巡检、聚合、发现）。

---

## 5. 变量全部暴露

- 所有变量都是**公开属性**：`query.variables(scope)` 可读，WebUI 有变量面板，trace 中逐拍展示变化（旧值 → 新值）。
- 变量带 schema：名称、类型、描述、初值、是否可写。
- **写入仍只属于所属实例**；跨图读取一律读"上次提交值"（`11` §5），因此暴露不会破坏一致性。
- 变量可被 UI 直接编辑（调试用），写入走 `instance.setVariable`，记审计，不绕过图。

---

## 6. "图即代码"必须补齐的能力

| 代码世界 | 这里 | 现状 |
|---|---|---|
文件/模块 | GraphClass + JSON 源文件（可 diff、可 git、可评审） | 已有雏形（12-graph-json 待写） |
编译 | 校验：类型、绑定、唯一激活源、初值齐全、方法调用存在 | `04` §9 |
单元测试 | 试跑：给定激活 + 数据桩，返回逐段判定与变量变化 | 待建 |
调试器 | trace（按 `traceId` 聚合段）、变量面板、**断点**（某块命中即暂停） | trace 已有，断点待定 |
重构 | 重命名、查找引用（方法调用关系 = 图的依赖图） | 待建 |
依赖管理 | 类版本 + 实例迁移 | `12` 本文 §3 |
包管理 | 类/模板的导出导入 | 待定 |

---

## 7. 待定

| 项 | 候选 |
|---|---|
实例销毁时机 | **与挂点共存亡**（立即销毁）；死前激活 `on.kill` 可取消，除非强杀 |
频道挂哪些动态类 | **挂载表**（数据、可选、可禁用）；单频道可覆盖参数或换用变体类 |
静态图是否允许按 key 分区变量 | **禁止**（必须用该作用域的动态实例） |
断点与单步 | **暂不做**；只做 trace + 试跑 |
变量直改 | 允许（调试），需审计 + 仅开发模式开关 |
实例复活 | 不允许；取消销毁只是延后，挂点消失本身不可逆 |
