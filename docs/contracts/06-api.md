# 对外接口契约（06）

> 前置：`00-decisions.md` D34–D36。本文定义 `AgentLilara.Api` v1。WebUI 是纯消费者；接口先冻结，UI 后重做。

---

## 1. 形态与约束

- 传输：HTTP/JSON + WebSocket；默认绑 `127.0.0.1`，token 鉴权（`Authorization: Bearer` / `X-Api-Token`）。
- 版本：路径前缀 `/v1`；破坏性变更走 `/v2`，`/v1` 保持兼容期。
- 三类操作分开：
  - **Query**：只读，幂等，无审计（除敏感字段脱敏）。
  - **Command**：写入，走 `draft → 审批 → apply`（或按 governor 档位自动 apply），**全审计**。
  - **Subscription**：WS 单向推送，断线重连带 `lastSeq` 续传。
- UI 不得包含业务逻辑：字段校验、权限、副作用全部在内核侧。

---

## 2. 自描述

```
GET /v1/capabilities
{
  "api": "v1",
  "kernel": { "version": "x.y.z", "schemaVersion": 12 },
  "graphs": { "kinds": ["Static","Dynamic"], "nodeBases": ["on.*","read.*","logic.*","sink.*","module.*"] },
  "queries": [ { "id": "messages", "params": {...schema...}, "result": {...schema...} } ],
  "commands":[ { "id": "channel.attach", "params": {...}, "approval": "auto|required" } ],
  "subscriptions": [ { "id": "events", "payload": {...} } ],
  "features": { "fts": false, "wallclockDelay": false, "selfModify": "propose" }
}
```

`features` 让 UI 对未实现能力标灰（如 `wallclockDelay=false` 时延迟（ms）节点不可选）。

---

## 3. Query

| 端点 | 说明 |
|---|---|
`GET /v1/query/channels` | `scope=registered\|unattached\|platform\|all`、`adapter`、`kind`、`keyword`、`limit` |
`GET /v1/query/messages` | `scope=channel\|platform\|all`、`channelRef` 或 `{adapterId, platformChannelId}`、`keyword`、`timeFrom/To`、`sender`、`attachments`、`afterId/beforeId`、`limit` |
`GET /v1/query/sources` | `kind`、`enabled` |
`GET /v1/query/sessions` | `channelRef`、`type`、`state` |
`GET /v1/query/adapters` | 存活、能力、可执行动作 |
`GET /v1/query/plugins` | 插件清单与贡献 |
`GET /v1/query/resources` | 资源持有者、等待队列、最近丢弃原因 |
`GET /v1/query/park` | Park 队列快照（谁在等、何时到期） |
`GET /v1/channel/{ref}/routes` | 入向/出向两个有序列表 + 当前生效源 + 健康灯 |
`GET /v1/channel/{ref}/gaps` | 缺口列表与状态 |
`GET /v1/channel/{ref}/outbound` | 出向分段台账 |

消息引用统一 `msgRef = "{adapterId}:{localId}"`；未落盘消息为 `pending:{n}`。

---

## 4. Command（draft → 审批 → apply）

```
POST /v1/command/{id}          body: params + { "mode": "apply" | "draft", "rationale"?: string }
→ { "ok": true, "applied": true, "auditId": 123 }
→ { "ok": true, "proposalId": 45, "diff": {...} }        // mode=draft 或 governor=propose
```

| command | 说明 | 审批 |
|---|---|---|
`channel.attach` / `channel.detach` / `channel.update` | 频道登记 | auto（可配 required） |
`channel.routes.set` | 保存两个方向的优先级列表 | auto |
`channel.audit` | 触发强查对账（异步，返回 taskId） | auto |
`graph.upsert` / `graph.delete` | 保存图（经编译期校验） | required（环路需显式 ack） |
`graph.test` | 试跑（给定事件/facts，返回逐段判定） | auto |
`setting.write` | 写设置（按 `SettingDescriptor.Risk`） | 依风险 |
`proposal.apply` / `proposal.reject` | 审批提案 | required |
`snapshot.rollback` | 回滚到快照 | required |
`plugin.reload` / `plugin.enable` / `plugin.disable` | 插件生命周期 | required |
`breaker.set` | 调整 governor 档位 | **human only**（API 直接拒绝，需进程外操作） |

---

## 5. Subscription

```
WS /v1/stream?lastSeq=12345
→ { "type": "message.ingested",  "seq": ..., "channelRef": ..., "msgRef": ... }
→ { "type": "graph.activated",   "seq": ..., "graphId": ..., "sessionId": ... }
→ { "type": "resource.dropped",  "seq": ..., "resourceId": ..., "reason": ... }
→ { "type": "session.state",     "seq": ..., "sessionId": ..., "state": ... }
→ { "type": "gap.detected",      "seq": ..., "channelRef": ..., "kind": ... }
→ { "type": "audit.appended",    "seq": ..., "actor": ..., "action": ... }
→ { "type": "log",               "seq": ..., "level": ..., "span": ... }
```

背压：服务端按订阅者限速，超限合并（同类型同频道 100ms 窗口），不丢关键事件（`gap/audit/dropped` 必送）。

---

## 6. 兼容与迁移

- WebUI 切出进程时先用旧 UI 打新 API，逐页对照功能清单，确认对齐后再换皮。
- 旧 HttpBridge 外部客户端：未接入频道返回 `{ "ok": true, "activated": false, "skipped": "unattached-channel" }`；提供显式 `attach_if_missing` 参数（默认 false）。
- 鉴权失败统一 `401 {"ok":false,"error":"unauthorized"}`，不做信息泄露。

---

## 7. 待定

| 项 | 说明 |
|---|---|
WebSocket 与 SSE 二选一 | 当前定 WS（双向重连友好） |
分页风格 | 统一 `afterId/beforeId + limit`，不使用 offset |
大字段（RawRef/长文本） | `?expand=raw` 显式请求，默认不返回 |
