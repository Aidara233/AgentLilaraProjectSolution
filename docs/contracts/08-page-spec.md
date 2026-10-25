# 页面契约（08）

> 前置：`00-decisions.md` D34–D36；`06-api.md`。WebUI 由"渲染器 + schema"组成，页面是数据，不是代码。

---

## 1. PageSpec

```csharp
public sealed record PageSpec(
    string Route,            // "activation/rules/{graphId}"
    string Title,
    PageMeta Meta,           // Group / Order / Icon / ShowInNav / DefaultCollapsed
    string[] RequiredApi,    // 依赖的 capabilities 项；缺失则该页不可用
    BlockSpec[] Blocks);

public sealed record BlockSpec(
    string Id,
    string Type,             // Table | Form | Tree | Editor | Stats | Stream | Canvas | RuleBuilder
    string DataSource,       // query id 或 command id
    JsonNode Schema,         // 该 block 的字段/列/节点定义
    JsonNode? Options);      // 分页、排序、折叠、只读等
```

贡献方式：
- 内核内置页与插件页同构：`ui/*.json`（插件）或内置 JSON（内核）。
- 页面路由冲突在注册期告警（沿用现有 `ProviderRegistry` 的冲突诊断语义）。

---

## 2. Block 类型

| Type | 用途 | 关键 Schema |
|---|---|---|
`Table` | 列表：频道、消息、缺口、审计、插件 | `columns[]`（含类型/宽度/排序）、`filters[]`、`rowActions[]`、分页 |
`Form` | 设置、适配器配置、频道属性 | 由 `SettingDescriptor.Schema` 直接生成 |
`Tree` | 会话池树、频道分组 | `nodeKey`、`childrenKey`、`labelPath` |
`Editor` | 图 JSON / 提示词 / 脚本文本 | `mode`（json/text/csharp）、`validate`（服务端校验 id） |
`Stats` | 命中曲线、激活次数、资源占用 | `metrics[]`、`window` |
`Stream` | 日志/事件实时 | `subscription`、`bufferSize`、`filter` |
`Canvas` | **图编辑器**：Blender 式节点画布 | 见 §3 |
`RuleBuilder` | 列表式触发器编辑（简单规则的快捷面） | 见 §4 |

---

## 3. Canvas block

Canvas 是**通用渲染器**，不含任何业务节点的画法——节点外观来自 `NodeDescriptor`。

```jsonc
{
  "type": "Canvas",
  "schema": {
    "graphKind": "Data",                 // Data | Signal
    "nodeCatalog": "capabilities.nodes", // 节点目录来源（含插件节点）
    "layout": "layered",                 // 继承层叠片
    "edgeStyles": {
      "event":      { "line": "solid",  "width": 2 },
      "eventLater": { "line": "dashed", "width": 3, "segmentMarker": true },
      "value":      { "line": "dotted","width": 1 }
    },
    "inheritLayers": { "enabled": true, "thinBorder": true, "hoverHighlight": true },
    "validators": ["unbound-input","type-mismatch","cycle","sink-no-output","default-not-const"],
    "actions": ["save","test","dryrun","foldToTemplate","mute"]
  }
}
```

要求：
- 插口类型颜色、必填标记、默认值只读展示，全部取自 `SocketSpec`。
- 跨拍边必须画段边界标线；两侧底色不同。
- 继承层以细边界叠片显示；悬停高亮"该层新增/覆盖"的插口。
- 保存前必跑 `validators`；环路需显式 ack 才能保存。
- 端口/节点来自 `/v1/capabilities`，插件新增节点无需改前端。
- **按网络分页**：一张图文档可含多个互不相连的网络，画布默认"一网络一页"（页签或左栏网络列表），切换页签即切换编辑/校验作用域；跨网络连线需显式确认（会合并网络）。渲染时相邻网络不得共画布，避免误认为相连。

---

## 4. RuleBuilder block

给"简单规则"用，读写同一份图 JSON 的受约束子集：

```
[源: channel.watch(群A)] [条件: 被@ 或 含关键词] [装配: 快速回应预设] [提示词: …] [waitMs: 0]
```

- 只能表达"源 → 逻辑 → 方法调用"的单链；一旦需要分支、多模块调用或读动态图状态，按钮"在画布中打开"。
- 列表视图与画布视图互不改写对方的结构（保存时做结构校验，冲突则提示）。

---

## 5. 首批页面

| Route | Blocks | 数据源 |
|---|---|---|
`/dashboard` | Stats + Stream | `query.sessions` + `events` 订阅 |
`/channels` | Table（已接入/未接入双栏）+ Form | `query.channels`、`channel.attach/detach` |
`/channels/{ref}` | Form（属性）+ Table（路由）+ Table（缺口）+ Table（出向台账） | `channel.routes.*`、`channel.audit` |
`/activation` | Table（图列表）+ Stats（命中/丢弃） | `query.graphs` |
`/activation/rules/{graphId}` | RuleBuilder ⇄ Canvas | `graph.upsert`、`graph.test` |
`/activation/signals` | Table（端口目录）+ Canvas（信号图） | `query.signals` |
`/activation/test` | Form（选事件/贴 facts）+ Table（逐段判定） | `graph.test` |
`/resources` | Table（持有者/队列）+ Table（丢弃流水） | `query.resources`、`query.park` |
`/adapters` | Table + Form + Table（能力/动作） | `query.adapters` |
`/plugins` | Table（清单/贡献/权限）+ 动作按钮 | `query.plugins`、`plugin.*` |
`/settings` | Form（由 SettingDescriptor 生成） | `setting.write` |
`/governor` | Form（只读展示 + 人类操作说明）+ Table（提案）+ Table（审计） | `query.proposals`、`query.audit` |
`/logs` | Stream + Table | `logs` 订阅 |
`/images`、`/memory`、`/review`、`/vision` | 沿用现有能力，改由 query 驱动 | 复用 |

---

## 6. 约束

1. **UI 不做业务校验**：所有 schema 校验在内核侧，UI 只展示错误。
2. **不做隐式写**：所有写操作映射到 `command`，都可审计。
3. **能力缺失即标灰**：`capabilities.features` 缺项时对应控件禁用并给原因（如 `wallclockDelay=false`）。
4. **导航由 `PageMeta` 生成**：插件贡献的页面自动出现在导航，不需要改外壳代码。
5. **鉴权**：默认 `127.0.0.1` + token；页面本身不判权限，权限在内核 command 层。
