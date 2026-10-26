# 图源码格式（13）

> 状态：**与实现同步**（`src/AgentLilara.GraphRuntime/Serialization/`）。
> 实现为唯一事实来源；本文与代码不一致时以代码为准，并请回来修本文。

---

## 1. 文件形态

一个图类（静态类或动态类）一个 JSON 文件：

```json
{
  "classId": "preset.mention",
  "kind": "static",
  "version": 1,
  "nodes": [
    { "id": "on",      "type": "on.message", "x": 80,  "y": 120 },
    { "id": "mention", "type": "event.mention", "x": 260, "y": 40 },
    { "id": "gate",    "type": "gate.if", "x": 260, "y": 200 },
    { "id": "text",    "type": "event.get", "params": { "field": "text" }, "x": 440, "y": 80 },
    { "id": "session", "type": "read.const", "params": { "value": "default" }, "x": 440, "y": 260 },
    { "id": "prompt",  "type": "sink.prompt", "x": 620, "y": 160 }
  ],
  "links": [
    { "from": "on.event",       "to": "mention.event" },
    { "from": "on.event",       "to": "gate.event" },
    { "from": "mention.result", "to": "gate.cond" },
    { "from": "on.event",       "to": "text.event" },
    { "from": "gate.event",     "to": "prompt.event" },
    { "from": "session.value",  "to": "prompt.sessionRef" },
    { "from": "text.value",     "to": "prompt.text" }
  ]
}
```

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
`classId` | string | ✅ | 类标识，同类升级靠它 + `version` |
`kind` | `static` \| `dynamic` | ✅ | 静态类（无状态图）/ 动态类（有状态图） |
`version` | int | | 默认 1；当前仅记录，迁移逻辑未实现 |
`variables[]` | array | | 内部存储声明（见 §3） |
`nodes[]` | array | ✅ | 节点（见 §2） |
`links[]` | array | ✅ | 连线（见 §4） |

---

## 2. 节点

```json
{ "id": "read", "type": "event.get", "params": { "field": "text" }, "x": 200, "y": 120 }
```

| 字段 | 说明 |
|---|---|
`id` | 图内唯一，非空 |
`type` | 必须命中节点注册表（§5） |
`params` | 类型专属参数；**未知参数直接报错**（防止错拼被静默忽略） |
`x` / `y` | 编辑器坐标，运行期不用 |

## 3. 变量

```json
{ "name": "count", "type": "int", "initial": 0, "mutable": true, "description": "..." }
```

- `type`：`int` | `bool` | `string`，默认 `int`
- `initial`：必填，且必须与 `type` 匹配
- `mutable`：默认 `true`；为 `false` 时 `write.variable` 指向它应在加载期被拒（待实现）

## 4. 连线

```json
{ "from": "on.event", "to": "mention.event" }
```

- 端点写法固定为 `节点id.端口名`；解析取**最后一个点**（节点 id 可以含点）
- 每个输入口必须**恰好**被一条连线喂满
- 汇块不得有下游

---

## 5. 节点注册表（当前实现）

| type | 类别 | params | 输入 | 输出 |
|---|---|---|---|---|
`on.message` | 激活源（每图恰一个） | — | — | `event`（`InboundEvent`）, `channel` |
`on.clock` | 激活源（动态图时钟，归档保留） | — | — | `event` |
`on.external` | 激活源（外部信源事件） | — | — | `event` |
`read.const` | 数据源 | `value`（bool/int/string） | — | `value` |
`read.variable` | 数据源 | `name` | — | `value` |
`read.mount` | 数据源 | `name`, `default`（可选） | — | `value` |
`event.get` | 算子 | `field`（白名单） | `event` | `value` |
`event.ext` | 算子 | `name`（外部载荷键） | `event` | `value` |
`event.mention` | 算子 | — | `event` | `result` |
`logic.and` | 算子 | — | `a`, `b` | `result` |
`logic.or` | 算子 | — | `a`, `b` | `result` |
`logic.select` | 算子 | — | `cond`, `a`, `b` | `result` |
`gate.if` | 算子（闸门） | — | `event`, `cond` | `event` |
`compare.ge` | 算子 | — | `value`, `threshold` | `result` |
`compare.eq` | 算子 | — | `a`, `b` | `result` |
`math.add` | 算子 | — | `a`, `b` | `result` |
`list.first` | 算子 | — | `list` | `value`, `found` |
`write.variable` | 数据汇（变量） | `name` | `value` | — |
`sink.send` | 动作（出意图） | — | `event`, `text` | — |
`sink.prompt` | 动作（出意图） | — | `event`, `sessionRef`, `text` | — |
`sink.inject` | 动作（出意图） | — | `event`, `sessionRef`, `text` | — |
`sink.set` | 动作（出意图） | — | `event`, `key`, `value` | — |
`sink.notify` | 动作（出意图） | — | `event` | — |
`module.<id>.<method>` | 动作（即时调用） | — | `event` + 每个参数一个口 | — |
`read.<id>.<property>` | 数据源（属性读） | — | — | `value` |
`write.<id>.<property>` | 数据汇（属性写） | — | `event`, `value` | — |

内建节点由 `BuiltInNodeFactory` 提供；`module.* / read.<id>.* / write.<id>.*` 由 `ModuleHost` 按模块清单自动投影（`14-modules.md` §3）。
加载含模块节点的图必须显式给工厂：

```csharp
var host = new ModuleHost(ModuleCatalog.FromTypes(new[] { typeof(IMyModule) }));
host.Bind("my.module", implementation);
var graph = GraphLoader.Load(json, host.Registry);   // 内建 + 模块
```

`GraphLoader.Load(json)`（不带工厂）只认识内建节点，遇到模块节点会报 `unknown type`。

---

## 6. 加载期校验（全部实测）

| 检查 | 异常类型 | 信息示例 |
|---|---|---|
JSON 语法错误 | `GraphLoadException` | `invalid json: ...` |
`classId` 为空 | `GraphLoadException` | `classId is required` |
`kind` 缺失/非法 | `GraphLoadException` | `unknown kind 'x'` |
节点 type 未注册 | `GraphLoadException` | `node 'sink': unknown type 'sink.teleport'` |
未知事件字段 | `GraphLoadException` | `node 'text': unknown event field 'txt' (allowed: text, sender, ...)` |
模块未绑定实现 | `GraphLoadException` | `node 'call': module 'demo.echo' has no bound implementation` |
节点 id 为空 | `GraphLoadException` | `node of type 'sink.prompt' has empty id` |
参数未知 | `GraphLoadException` | `node 'on': unknown param 'channelRefs'` |
参数类型不符 | `GraphLoadException` | `param 'value' expected integer` |
变量初值缺失/类型不符 | `GraphLoadException` | `variable 'count': expected int initial value` |
连线格式错误 | `GraphLoadException` | `link 'from' must be 'node.port', got 'onevalue'` |
节点 id 重复 | `GraphValidationException` | `duplicate node id 'and'` |
激活源数量 ≠ 1 | `GraphValidationException` | `must have exactly one activation source, found 0` |
输入口未绑定 | `GraphValidationException` | `input 'gate.cond' is unbound` |
同一输入口被接两次 | `GraphValidationException` | `bound more than once` |
汇块有下游 | `GraphValidationException` | `sink node 'sink' must not have downstream links` |

---

## 7. 运行期行为（与源码的关系）

- 加载即编译：`GraphLoader.Load` 走完全部校验后才返回 `GraphClass`。
- 静态类：一次求解；有环会在求解期判 **SolveInvalid**（`unresolved` 列出环上节点）。
- 动态类：`on.clock` 为唯一激活源；变量按**快照读、拍末提交**。
- 求值顺序：按 `nodes` 数组顺序 → 节点 id 字典序，保证同一源码同一输入得到同一序列。

---

## 8. 尚未实现（明确缺口）

| 项 | 说明 |
|---|---|
保存/序列化回 JSON | 只有加载器；编辑器与版本管理需要 `Save` |
`version` 迁移 | 字段已留，升级迁移未实现；当前建议"变量只增不改" |
`mutable: false` 校验 | 未拦 `write.variable` 指向只读变量 |
`x` / `y` | 已解析但不使用（留给画布） |
挂载信息 | 不写在图文件里，属 `GraphMounts` 表（`12-mounting-and-editor.md` §3.1） |
类型系统 | 端口当前为字符串名，无静态类型检查（`Cell` 运行期带 object），后续需 `PortDecl` 化 |
