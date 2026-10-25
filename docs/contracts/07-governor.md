# 授权、自修改与审计（07）

> 前置：`00-decisions.md` D29–D33。本文定义设置描述符、断路器、审批流、审计与快照回滚。

---

## 1. 工具分层

| 层 | 内容 | 门禁 |
|---|---|---|
T0 读 | 各类 query、`mind_status`、`query_adapters` | 默认开 |
T1 无副作用动作 | thinking、笔记、pin、`graph.test`（试跑） | 默认开 |
T2 外部副作用 | `sink.*`、`http_request`、`ssh` | 需资源/频道授权 |
T3 配置写 | 改图、改路由、改适配器配置 | 审批（按 `Risk`） |
T4 自修改 | 改提示词/组件装配/工具白名单/核心配置 | 审批 + 审计 + 快照 |
T5 元修改 | 改授权等级、改断路器、改审计保留 | **人类专属，模型永不可及** |

---

## 2. 设置即数据

```csharp
public sealed record SettingDescriptor(
    string Key,            // "activation.rules.homeassistant"
    JsonNode Schema,       // JSON Schema：类型/范围/枚举 → UI 自动生成表单 + 服务端校验
    string Scope,          // global | adapter:{id} | channel:{ref} | graph:{id}
    RiskLevel Risk,        // Low | Medium | High | Critical
    ApprovalMode Approval, // Auto | Required | HumanOnly
    bool SelfModifiable,   // false → 模型完全不可写（T5 项一律 false）
    string Description);
```

- `config_read` / `config_write` **一对通用工具**覆盖全部设置；WebUI 表单由 `Schema` 生成。
- 新增设置 = 新增一条描述符，不需要新工具、不需要新页面。
- `SettingDescriptor` 自身也是数据（`Storage/Settings/registry.json`），由人类维护；模型只读。

---

## 3. 断路器（governor）

```
Storage/Auth/governor.json      ← 人类可写，模型只读
{
  "selfModify": "off",          // off | propose | apply-with-audit | autonomous
  "allowlist": ["graph.*", "channel.routes.*"],
  "denylist":  ["governor.*", "audit.*", "setting.registry.*"],
  "scriptPolicy": { "enabled": true, "maxMs": 50, "maxStatements": 200 }
}
```

| 档位 | 行为 |
|---|---|
`off` | 所有 T3/T4 写入被拒（含提案） |
`propose` | 只产生 `Proposals`，不落地 |
`apply-with-audit` | allowlist 内自动落地 + 审计 + 快照 |
`autonomous` | 除 denylist 与 T5 外自动落地 |

**硬约束**（代码内硬编码，配置文件不可覆盖）：
1. `governor.*`、`audit.*`、`setting.registry.*` 永远在 denylist。
2. `Approval = HumanOnly` 的设置永远拒绝模型写入。
3. `selfModify` 档位变更只能通过进程外手段（手工编辑文件 / CLI），API 的 `breaker.set` 直接拒绝。

---

### 3.1 授权依据：权限等级，而非信任等级

| 项 | 结论 |
|---|---|
判定依据 | `Person.PermissionLevel`（权限等级）决定能做什么；`TrustLevel`（信任等级）只影响对话风格与内容尺度，**不参与权限判断** |
改图入口 | 现阶段**只有 WebUI 经 `Api` 的 command**（`graph.upsert/delete/test`）；**会话内不提供改图工具** |
模型自修改 | 整体后置。接口先留（`sink.setting.propose`、`config_write`），但 `governor.selfModify = off` 期间一律拒绝 |
多用户 | 权限等级映射到 T0–T5 层；WebUI 的每个 command 按等级校验，不信任前端 |
断路器 | 仍是人类专属（T5），与权限等级无关，任何等级都不能改它 |

---

## 4. 审批流

```
模型/UI 发起 → draft（校验 schema + 权限）
   → Proposals { Kind, DiffJson, Rationale, ExpectedEffect }
   → 审批（人 / governor 自动）
   → apply（原子：写设置 + 落审计 + 拍快照）
   → 失败回滚到上一步快照
```

提案失败不得留下半成品：写操作统一走"事务 + 快照"，无部分生效。

---

## 5. 审计

```sql
AuditLog(Id, Seq, Actor, Action, Target, BeforeHash, AfterHash, At, Note)
RegisterAudit(Id, Seq, GraphId, SpliceId, Old, New, At)   -- 状态变更
ResourceAudit(Id, Seq, ResourceId, AttemptId, WaitMs, Outcome, WaitedMs, At)
SinkAudit(Id, Seq, GraphId, SinkId, Outcome, At)
```

- append-only；不提供删除 API（保留策略由人类在进程外执行）。
- `BeforeHash/AfterHash` 指向内容快照，可精确 diff。
- 审计可被 `query` 读取、被 WS 订阅推送（`audit.appended`）。

---

## 6. 快照与回滚

```
Storage/Snapshots/{scope}/{timestamp}-{hash}.json
```

- 覆盖范围：设置、图、路由、提示词、组件装配、图实例变量与模块状态。
- `snapshot.rollback(id)` 是一等工具/命令，回滚本身写审计。
- 快照去重按内容哈希，避免频繁写入。

---

## 7. 脚本（进程内 Roslyn）

| 项 | 约束 |
|---|---|
引用 | 白名单：`System.Runtime`、`System.Text.RegularExpressions`（可选 `System.Math`） |
禁用 | IO、网络、反射、`unsafe`、`Environment`、线程创建 |
预算 | 语句数上限（默认 200）、墙钟上限（默认 50ms），超限中止并告警 |
输入 | 只读 `FactsView` + `Now`；不可访问服务容器 |
输出 | `bool`（或 `Bool` 值口） |
写入 | 脚本本身属于 T4（配置写），需审批 |
审计 | 脚本内容随图快照存档，可回溯 |
标记 | 声明 `Nondeterministic` 的脚本在试跑时提示"重放可能不同" |

规避灾难性回溯：`filter.regex` 默认 `RegexOptions.NonBacktracking`；脚本内的正则同样限制。

---

## 8. 待定

| 项 | 说明 |
|---|---|
进程外沙箱 | 当出现"不可信来源的规则包"时再引入 |
脚本预算默认值 | 依实际负载调；先按 200 语句 / 50ms |
快照保留 | 由人类策略控制，默认保留最近 50 份/scope |
