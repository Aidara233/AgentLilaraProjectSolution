# 模块契约（14）

> 依据：`00-decisions.md` D56（模块边界 = 图的粒度）、`11-object-model.md`（类/方法/属性/变量）。
> 本文定义：什么是一个模块、怎么声明、怎么投影成图节点、调用规则与失败语义。

---

## 1. 什么是模块

**模块 = 一段自成体系的代码，只暴露接口。**

| 模块对外 | 图中表现 | 内部（图不可见） |
|---|---|---|
生命周期入口 | 激活源 | 初始化、内部状态、资源 |
方法 | 方法节点（带实参、可带 `waitMs`） | 工具循环、重试、超时、算法 |
属性（读） | 数据源节点 | 内部结构 |
属性（写） | 数据汇节点 | |
内部变量 | **不暴露** | 全部 |

典型模块：`agent.loop`（工具循环）、`email`（收信）、`homeassistant`（实体状态）、`throttle`（节流，动态图也是模块）、`sqlite`（存储）。

**禁止**：在模块内部再画图（图套图）。需要中途插条件 → 拆成两个模块。

---

## 2. 声明方式（已实现）

```csharp
[Module("agent.loop", Version = "1.0.0", RequiredCapabilities = new[] { Capabilities.Net })]
public interface IAgentLoop
{
    // 生命周期入口：由框架调用，不出现在画布上
    [Lifecycle("mount")] Task OnMountAsync();
    [Lifecycle("kill")]  Task<bool> OnKillAsync();

    // 方法：一个方法 = 一个激活汇节点
    [Method(Returns = "reply", SideEffect = true, SupportsWait = true)]
    Task<Reply> RunAsync(string message, int budget = 3, CancellationToken ct = default);

    // 属性（读）：数据源节点
    [Property(ReadOnly = true)] int Calls { get; }

    // 属性（写）：额外一个数据汇节点
    [Property] string? Nickname { get; set; }
}
```

约束（生成期强制，违反即 `ModuleDescriptorException`）：

| 规则 | 说明 |
|---|---|
类型必须是接口且带 `[Module]` | id 非空、无空格；`Version` 非空；能力名必须在 `Capabilities` 里 |
每个公开方法二选一 | `[Method]` 或 `[Lifecycle]`；**未标注直接失败**（不给"图上凭空少一个节点"） |
属性必须标 `[Property]` | 有 `setter` 且未 `ReadOnly` → 生成写节点 |
参数类型受限 | `bool / int / long / double / string / enum / record`（含 `list:*`）；`object`、委托、接口、抽象类一律拒绝 |
`CancellationToken` | 由框架注入，**不占端口** |
名字唯一 | 投影后的方法与属性节点名不得重复（`RunAsync` → `run`） |

---

## 3. 模块描述符（已实现）

```csharp
public sealed record ModuleDescriptor(
    string Id, string Version,
    IReadOnlyList<MethodDescriptor> Methods,
    IReadOnlyList<PropertyDescriptor> Properties,
    IReadOnlyList<ActivationDescriptor> Activations,
    IReadOnlyList<string> RequiredCapabilities);

public sealed record MethodDescriptor(
    string Name, string ReturnType, IReadOnlyList<string> Returns,
    IReadOnlyList<ParamDescriptor> Params,
    bool SideEffect, bool SupportsWait, bool Nondeterministic, int TimeoutMs);

public sealed record ParamDescriptor(string Name, string Type, bool Optional, Cell Default);
public sealed record PropertyDescriptor(string Name, string Type, bool ReadOnly);
public sealed record ActivationDescriptor(string Phase, string MethodName);
```

- **由反射 + 特性生成**（`ModuleDescriptorGenerator.Generate(Type)`），编译期与实现保持一致；不手写 JSON。
- `ModuleCatalog.FromTypes(...)` 汇总成清单；`ModuleCatalog.ToJson()` 是给编辑器读的快照（**以代码为准**）。
- 节点类型命名：

| 接口成员 | 节点类型 | 图类别 |
|---|---|---|
方法 `RunAsync` | `module.<id>.run` | 激活汇（方法） |
属性 `Calls`（只读） | `read.<id>.calls` | 数据源（属性读） |
属性 `Nickname`（可写） | `read.<id>.nickname` + `write.<id>.nickname` | 数据源 + 数据汇 |

- `ModuleHost` 负责投影与绑定：`Bind(moduleId, implementation)` 显式绑定实现；**没绑定实现的模块，含它的图加载即失败**（`GraphLoader.Load(json, host.Registry)`）。

---

## 4. 调用规则

| 方向 | 允许 | 说明 |
|---|---|---|
图 → 模块 | ✅ 同步调用方法 | 实参来自上游端口；调用发生在阶段③ |
模块 → 图 | ⚠️ 只能**脉冲**（异步、无返回） | 防止"代码等图、图等代码"的死结 |
模块 → 模块 | ✅ 直接代码调用 | 不计入图；但要出现在模块依赖声明里 |
图 → 图 | 仅 `sink.pulse` | 同上，单向（未实现） |

方法节点的形态：

| 项 | 规则 |
|---|---|
输入口 | `event`（触发）+ 每个参数一个端口；`CancellationToken` 不占端口 |
可选参数 | 可以不接线（`IOptionalInputs`），取值用 C# 默认值 / `[Param(Default=...)]` |
输出口 | **无**。返回值进 `SinkOutcome.Outputs` 台账，不接端口（续延接线未实现） |
触发条件 | `event` 已知 + 必需实参全部已知；**实参类型不符则调用失败**（`SinkOutcome.Failed`，不猜不转） |
求值阶段 | 求解期只登记 `SinkCandidate`，阶段③才真正调用（求解无效时零副作用） |

`waitMs`：方法可声明 `SupportsWait`，调用时给数字口；`0` = 忙则立即放弃。等待由框架排队，模块不感知。**未实现**。

返回 `Task` / `ValueTask` 的方法目前走**同步桥**（`GetAwaiter().GetResult()`）：内核还没有异步执行管线，长任务会阻塞激活线程（见 §10）。

---

## 5. 生命周期与实例

| 事件 | 行为 | 状态 |
|---|---|---|
模块实例化 | 由宿主在 `Bind` 时注入实现 | 已实现（手动绑定；工厂/注入容器未实现） |
`[Lifecycle("mount")]` | 实例就绪后调用一次 | **未实现**（描述符已生成，框架尚未调用） |
`[Lifecycle("kill")]` | 挂点消失前调用；返回 `true` 取消销毁 | **未实现** |
模块升级 | 按 `Version` 迁移；破坏性变更 → 拒绝加载并列出受影响的图 | **未实现** |
状态 | 模块自管；图中只能通过属性读"上次提交值" | 已实现 |

---

## 6. 失败语义

- 模块**不得向外抛异常**：必须转成结果类型（`Result<T>` 或 `Error` 记录）。
- 框架捕获未处理异常 → `SinkOutcome.Failed(nodeId, message)`，**不把异常抛给激活调用方**（已实现）。
  记 `NodeAudit` 与激活 `runtime.error`（含模块 id、方法、消息）**尚未实现**。
- 汇块（方法节点）本身不报错：它只是接口壳；失败归模块。
- 超时：框架按方法声明的 `TimeoutMs`（默认 30s）中止并记错。**未强制**（需要调度器）。
- 实参类型不符：调用不发生，`SinkOutcome.Failed` 带端口名与期望类型（不做隐式转换，除 `int↔long↔double` 与 enum 名）。

---

## 7. 权限与能力

```csharp
[Module("email", RequiredCapabilities = new[] { Capabilities.Net, Capabilities.Disk })]
```

- 能力：`Net` / `Disk` / `Db` / `Exec` / `Secret`。
- 运行时门禁：模块只能使用声明的能力；未声明的调用被拒并记审计。
- 插件模块的权限在 `plugin.json` 中另行声明（`07-governor.md`）。

---

## 8. 测试

| 层 | 内容 |
|---|---|
契约测试 | 每个模块一套 conformance：方法签名、返回类型、错误路径、超时、取消 |
图集成 | 用模块桩（stub）验证接线；真实模块只在端到端测试里出现 |
试跑 | 编辑器给方法节点"喂样例实参"，断言返回与副作用（副作用在沙箱中拦截） |

---

## 9. 反模式

| 反模式 | 问题 |
|---|---|
把模块做成"参数化开关集合"（十几个 bool） | 调用点无法阅读；应拆成多个方法 |
在模块里偷偷跨模块共享可变状态 | 破坏"只读上次提交值"，时序问题不可复现 |
方法返回大对象（整段会话历史） | 图与模块边界被数据量击穿；应返回摘要 + 游标 |
模块内部再画图 | 调试成本指数上升（D56） |
模块吞异常返回 `null` | 上游无法区分"没有"与"失败"；必须返回 `Result` |

---

## 10. 尚未实现

| 项 | 说明 |
|---|---|
异步执行管线 | 返回 `Task` 的方法走同步桥；真实异步（不阻塞激活线程）需要调度器 |
生命周期调用 | `[Lifecycle]` 已进描述符，框架尚未在实例化/销毁时调用 |
返回值接线（续延） | 返回值只在 `SinkOutcome.Outputs` 里；把它接进变量或下一拍（续延放行）未实现 |
record 型实参 | 类型允许，但图上没有构造 record 的节点（`read.const` 只产 bool/int/string） |
能力门禁 | 能力已声明进描述符；运行时拒绝未声明调用未实现 |
模块版本迁移 | 未实现（建议先只支持"方法只增不删"） |
`SupportsWait` / `waitMs` | 未实现 |
超时强制 | `TimeoutMs` 只记录，不执行 |
审计落盘 | `NodeAudit` / 异常记录未落盘 |

---

## 11. 参考模块：`agent.loop`（契约已声明 + 脚本化参考实现）

代码：`src/AgentLilara.GraphModules/Agent/AgentLoop.cs`（契约）、
`src/AgentLilara.GraphSandbox/Agent/ScriptedAgentLoop.cs`（脚本化实现，不联网）。

```csharp
[Module("agent.loop", Version = "0.1.0", RequiredCapabilities = new[] { Capabilities.Net })]
public interface IAgentLoop
{
    [Lifecycle("mount")] Task OnMountAsync();
    [Lifecycle("kill")]  Task OnKillAsync();

    [Method(SideEffect = true, Returns = "reply", TimeoutMs = 120_000)]
    Task<AgentReply> RunAsync(string sessionRef, string text, CancellationToken ct = default);

    [Method(SideEffect = true)] Task<bool> CancelAsync(string sessionRef, CancellationToken ct = default);

    [Property] string? Model { get; set; }          // 配置走属性
    [Property] int ToolBudget { get; set; }
    [Property] string? SystemPrompt { get; set; }
    [Property(ReadOnly = true)] int ActiveSessions { get; }
    [Property(ReadOnly = true)] string State { get; }
}
```

投影出的节点：`module.agent.loop.run`、`module.agent.loop.cancel`、
`read/write.agent.loop.{model,toolBudget,systemPrompt}`、`read.agent.loop.{activeSessions,state}`。

**为什么配置走属性而不是大 record 参数**：图能产出的值目前只有标量（`read.const` / 变量 / 消息文本）；
把 `config{prompt,model,tools,budget}` 塞进一个 record 参数会逼着图去构造 record（做不到，见 §10）。
属性 + `write.*` 节点把"配置"与"这次调用要处理的东西"分开，也符合对象模型（属性=读写，方法=动作）。

样例图（`AgentLilara.GraphSandbox/SampleGraphs.cs` 的 `AgentStaticJson`）：

```
on.message ──→ event.mention ──→ gate.if.cond
     │                              │
     └──────────────→ gate.if.event ─┴─→ module.agent.loop.run.event
event.get(text) ────────────────────→ module.agent.loop.run.text
read.const("file-session") ─────────────→ module.agent.loop.run.sessionRef
```

端到端已验证（`tests/AgentLilara.GraphSandbox.Tests/AgentLoopModuleTests.cs`）：
闸门放行 → 调用 → 模块内部跑完工具循环 → 回复整个对象落在 `SinkOutcome.Outputs["reply"]`；
`ToolBudget = 0` 时同一张图改走"预算不足"分支（配置经属性生效）。

**与旧实现的关系**：旧 `AgentCoreProcessor`（`AgentCore` / `RuntimeSession`，约 6900 行）内部完成同样的
"多轮 + 工具"工作，但类型不可复用（`internal`、耦合 DB/WebUI）。这里的契约是**新的、干净的形状**；
把旧核心接进来要做的是"适配器"：实现 `IAgentLoop`，内部转调旧 `RuntimeSession`，
并在 `ModuleHost.Bind("agent.loop", adapter)` 处注册。适配器需要能引用旧工程，因此属于本解决方案的适配层。
