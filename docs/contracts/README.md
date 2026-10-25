# 契约索引（README）

> 这是 `docs/contracts/` 的入口。**权威文档**是实现依据；**历史文档**仅保留推导过程，不得据此实现。
> 文档与代码冲突时：**代码为准**，并回来修文档。

---

## 1. 阅读顺序

```
00 决策记录          先看这个：所有已定与已推翻的东西
11 对象模型          类 / 方法 / 属性 / 变量；两类图；模块边界
04 执行语义          一次激活怎么算（三阶段、求解器、网络）
14 模块契约          模块怎么声明、怎么变成图节点
15 接线契约          骨架/事件/插槽、事件集、动作集、算子集（图能改什么）
05 节点目录          有哪些节点、插口长什么样
10 接线与模板        怎么连、内置模板、新建时默认接什么
13 图源码格式        JSON 字段与加载期校验（与实现同步）
12 挂载与编辑器      类 / 挂载 / 实例、生命周期、变量暴露
01 存储              分库、最小表契约、缓冲写
02 路由              优先级、强查、出向台账
03 事件与事实        InboundEvent、facts 命名空间、信号端口
06 对外接口          Api v1（Query / Command / Subscription）
07 授权与自修改      工具分层、governor、审计、快照
08 页面契约          PageSpec 与 block 类型
09 淘汰清单          哪些删、哪些重写、什么顺序
```

---

## 2. 文档状态

| 文档 | 状态 | 说明 |
|---|---|---|
`00-decisions.md` | **权威** | 决策台账（D1–D56），含已推翻项 |
`01-storage.md` | 权威（未实现） | 分库与表契约 |
`02-routing.md` | 权威（未实现） | 路由与强查 |
`03-events.md` | 权威（部分实现） | 事件与事实；信号端口清单 |
`04-graph-model.md` | **权威（已实现核心）** | 执行语义：三阶段、求解器、网络、校验 |
`05-nodes.md` | **权威（与代码同步）** | 节点目录；`event.*` 算子、`sink.*` 动作、`module.*` 均由清单投影 |
`06-api.md` | 权威（未实现） | Api v1 |
`07-governor.md` | 权威（未实现） | 授权与自修改 |
`08-page-spec.md` | 权威（未实现） | 页面 schema |
`09-retirement.md` | 权威 | 淘汰清单与顺序 |
`10-blocks-and-wiring.md` | 权威 | 接线规则与模板 |
`11-object-model.md` | **权威** | 对象模型；与 04 共同构成实现依据 |
`12-mounting-and-editor.md` | 权威（未实现） | 挂载、实例、编辑器能力 |
`13-graph-json.md` | **权威（与代码同步）** | 图源码格式 |
`14-modules.md` | **权威（已实现核心）** | 模块契约、描述符生成、节点投影与调用 |
`15-wiring-contract.md` | **权威（部分实现）** | 骨架/事件/插槽；事件集、动作集、算子集；预制图 |

**已废弃的概念**（除 `11` 的迁移对照表外，正文不再使用）：四类块（源/普通/汇/拼接）、`fired` 插口、`splice.*` 拼接块、`sessionType` 会话类型特化、`tick.hop`、express 会话。

---

## 3. 文档 ↔ 代码对照

| 文档 | 代码位置 |
|---|---|
`04` 执行语义 | `src/AgentLilara.GraphRuntime/{Solver,GraphInstance,Cell,SinkExecution}.cs` |
`05` 节点目录 | `src/AgentLilara.GraphRuntime/Nodes/{Sources,Logic,Sinks}.cs`、`Serialization/{BuiltInNodeFactory,NodeRegistry}.cs` |
`14` 模块契约 | `src/AgentLilara.GraphRuntime/Modules/*.cs`（特性、描述符生成、节点投影、`ModuleHost`） |
`14` §11 参考模块 | `src/AgentLilara.GraphModules/Agent/AgentLoop.cs`（契约）、`src/AgentLilara.GraphSandbox/Agent/ScriptedAgentLoop.cs`（脚本化实现） |
`15` 接线契约 | 事件：`Kernel/Contracts/InboundEvent.cs`；意图与动作清单：`Kernel/Intent.cs`；三层模型见文档 |
`05` 节点（算子/动作） | `Kernel/Nodes/{Sources,Logic,Sinks}.cs` |
`13` 图源码 | `src/AgentLilara.GraphRuntime/Serialization/*` |
`10` 接线 | `src/AgentLilara.GraphSandbox/SampleGraphs.cs`（样例） |
其余 | 未实现（旧实现仍在 `AgentLilaraProjectSolution/`） |

测试：`tests/`（46 条：求解、快照/边沿、加载校验、模块描述符与调用、事件→意图、沙箱轮询）。

**当前断点（下一步）**：图的两端已就绪（事件进、意图出），**骨架（adapter → ingest → store → session → outbound）尚未实现**——沙箱能产出 `session.prompt` 意图，但没有任何东西执行它。见 `15-wiring-contract.md` §9。

---

## 4. 文档公约

1. **一份文档只讲一件事**；跨文档引用用 `文件名 §小节`。
2. **不保留历史推导**：被推翻的结论写进 `00-decisions.md` 的决策台账，正文删除。
3. **标"未实现"**：契约先于实现是允许的，但必须显式标注，避免被误当成现状。
4. **与代码同步**：`13` 这类文档以代码为唯一事实来源；改字段必须同步改文档。
5. **不写"待定"超过两条**：待定项集中列在文末，超过两条说明设计未收敛，应回 `00` 讨论。

---

## 5. 术语表

| 术语 | 含义 |
|---|---|
**图 / 类** | 一份源码（JSON），静态类或有状态类 |
**静态类（无状态图）** | 唯一实例；一次激活一次求解；无环 |
**动态类（有状态图）** | 多实例；时钟沿触发；快照 → 同步更新；可有环 |
**激活源** | 生命周期入口；每图恰好一个 |
**方法（激活汇）** | 图对外的动作；被调方看到的就是它的激活源 |
**数据源 / 数据汇** | 属性读 / 属性写 |
**变量** | 实例内部存储；对外公开可读 |
**tick / 拍** | 一次激活的计算周期 |
**段** | 一个 tick 内的执行片段（阶段①–③） |
**trace** | 一条因果链，跨多个 tick；`traceId` 贯穿 |
**网络** | 图文档内的一个连通分量；校验与运行按网络独立 |
**模块** | 自成体系的代码，只暴露接口；图的节点由它投影而来 |
**Park / 等待条目** | 方法 `waitMs` 触发的框架级排队 |
**强查** | 查适配器自己的消息库（不是平台 API）以补缺口 |
