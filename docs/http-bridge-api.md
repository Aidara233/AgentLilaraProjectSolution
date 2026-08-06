# HttpBridge 适配器 API 文档

HTTP + WebSocket 混合协议适配器，为调试测试与外部系统接入提供直观的收发消息通道（替代/补充文件适配器）。

- **独立端口**：默认 `9876`，自建 Kestrel，不依赖 WebUI（`--test` 与正常模式均可用）
- **双向能力**：HTTP 发消息并阻塞等 bot 回复、查询聊天记录；WebSocket 实时收发
- **频道模型**：默认单频道一条连续对话线，可选 `channel` 分区

---

## 1. 配置

配置文件 `Storage/Adapter/http.json`（类型 `Type: "http"`）：

```json
{
  "Id": "http",
  "Type": "http",
  "Enabled": true,
  "AutoStart": true,
  "Settings": {
    "port": 9876,                  // 监听端口
    "token": "",                   // 访问令牌，空 = 不鉴权
    "defaultChannel": "http_debug",// 默认频道（客户端不带 channel 时的落点）
    "channelPrefix": "http_",      // 频道归属前缀（出向路由识别 http_xxx 归本适配器）
    "channels": [],                // 额外拥有（白名单）的频道名
    "defaultUser": "http-user",    // 缺省发言人
    "defaultIsPrivate": true,      // 缺省私聊标记（私聊视同提及 → 立即激活会话）
    "burstTimeoutMs": 30000,       // wait 模式硬超时 (ms)
    "busyObserveMs": 3000,         // 观察窗：等待消息入库的时间上限 (ms)
    "settleIntervalMs": 300,       // 稳定判定轮询间隔 (ms)
    "settleChecks": 3              // 连续 N 次空闲 = 突发结束
  }
}
```

### 频道模型

- **默认单频道**：不带 `channel` 的请求落到 `defaultChannel`，多客户端共享一条连续对话线（群聊语义），历史按频道累积、可随时拉取。
- **可选分区**：需要隔离不同对话线时，请求带 `channel` 指定（建议用 `channelPrefix` 前缀如 `http_room1`）。
- **用户自定**：发言用户由请求 `user`（→PlatformUserId）、`displayName`（→显示名）指定，"说啥是啥"，不做强制映射。一个频道可多用户。

---

## 2. HTTP 端点

所有端点前缀 `/api`。JSON 响应统一 `Content-Type: application/json`。

### 鉴权

配置了 `token` 时，请求需带（三选一）：
- `Authorization: Bearer <token>` 请求头
- `X-Api-Token: <token>` 请求头
- `?token=<token>` 查询参数

未通过返回 `401 {"ok":false,"error":"unauthorized"}`。`token` 为空时不鉴权。

### 2.1 `POST /api/send` — 发消息（阻塞等待回复 / 即发即走）

**请求体**（JSON）：

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `content` | string | 是 | 消息内容 |
| `channel` | string | 否 | 频道名，缺省用 `defaultChannel` |
| `user` | string | 否 | 发言人，缺省用 `defaultUser` |
| `displayName` | string | 否 | 发言人显示名 |
| `isPrivate` | bool | 否 | 私聊标记，缺省 `defaultIsPrivate`（true=私聊视同提及，立即激活） |
| `isMentioned` | bool | 否 | 是否 @ 提及，缺省 false |
| `replyTo` | string | 否 | 被回复的平台消息 id |
| `wait` | bool | 否 | true=阻塞等突发结束统一返回；false=即发即走。缺省 **true** |
| `timeoutMs` | int | 否 | wait 模式超时 (ms)，缺省 `burstTimeoutMs`(30000) |

**`wait=true` 响应**（阻塞直至 bot 本次突发处理完）：

```json
{
  "ok": true,
  "burstSeen": true,          // 是否观察到消息被引擎接收入库
  "timedOut": false,          // 是否因超时返回（bot 未在超时内处理完）
  "durationMs": 10519,        // 阻塞时长
  "replies": [                // 突发期间 bot 的全部回复（按入库顺序）
    { "id": 30, "content": "……", "senderName": "Lilara", "time": "2026-08-06T12:56:35.05+08:00" },
    { "id": 31, "content": "……", "senderName": "Lilara", "time": "2026-08-06T12:56:37.01+08:00" }
  ]
}
```

**`wait=false` 响应**：

```json
{ "ok": true, "accepted": true }
```

**语义说明**：
- `burstSeen=false`：消息未在观察窗（`busyObserveMs`）内入库——被命令拦截 / 权限拒绝 / 未激活，快速返回不空耗超时。
- `burstSeen=true` 但 `replies` 为空：消息已入库，但 bot 未在超时内回复（如普通消息攒权重节流未触发、或推理超时）。
- 建议调试用 `isPrivate=true`（或显式 `isMentioned=true`）让 bot 立即响应；不带则按 express 会话权重节流（普通消息攒批才触发）。

### 2.2 `GET /api/messages` — 聊天记录查询

**参数**（Query）：

| 参数 | 类型 | 说明 |
|------|------|------|
| `channel` | string | 频道名，缺省用 `defaultChannel` |
| `afterId` | int | **增量拉取**：返回该消息 id 之后的新消息（升序，用于"查收未读"） |
| `beforeId` | int | 返回该消息 id 之前的消息（升序） |
| `keyword` | string | 关键词搜索（按时间倒序取，转升序返回） |
| `limit` | int | 返回条数，1–200，缺省 50 |
| `offset` | int | 搜索分页偏移，缺省 0 |

**响应**：

```json
{
  "ok": true,
  "channel": { "id": 2, "name": "http_debug" },
  "messages": [
    { "id": 30, "userId": 0, "senderName": "Lilara", "content": "……", "isFromBot": true,
      "time": "2026-08-06T12:56:35.05+08:00", "platformMessageId": "http_00000001" },
    { "id": 31, "userId": 3, "senderName": "Tester", "content": "……", "isFromBot": false, "time": "…", "platformMessageId": null }
  ]
}
```

频道不存在时返回 `"channel": null` 与空 `messages`。

### 2.3 `GET /api/channels` — 频道列表

```json
{ "ok": true, "channels": [ { "id": 1, "name": "file" }, { "id": 2, "name": "http_debug" } ] }
```

### 2.4 `GET /api/status` — 状态

```json
{
  "ok": true,
  "adapter": {
    "id": "http", "platform": "http", "state": "Connected", "port": 9876,
    "defaultChannel": "http_debug", "messagesSent": 0, "messagesReceived": 0,
    "startedAt": "2026-08-06T12:49:08+08:00", "lastError": null
  },
  "engine": { "isIdle": true, "busyWorker": false }
}
```

`state` 取值：`Stopped / Connecting / Connected / Reconnecting / Error`。端口被占用时启动失败 → `state: Error` + `lastError`（不影响进程其他部分）。

---

## 3. WebSocket 端点

`ws://<host>:9876/ws?channel=<频道>&token=<token>`（token 可省略；`channel` 为连接时自动订阅的频道，可后续再 subscribe）

### 3.1 连接

握手成功后服务器先发一帧 welcome：

```json
{ "type": "welcome", "clientId": "c35d3c61…", "subscribed": ["http_debug"] }
```

### 3.2 客户端 → 服务端帧

| 帧 | 说明 |
|----|------|
| `{ "type":"subscribe", "channel":"http_debug" }` | 订阅频道，收该频道 bot 出向推送 |
| `{ "type":"unsubscribe", "channel":"http_debug" }` | 退订 |
| `{ "type":"send", "channel":"http_debug", "content":"…", "user":"tester", "isMentioned":false }` | 发消息（同 HTTP send 语义，即发即走；回复经订阅推送，不阻塞） |
| `{ "type":"ping" }` | 心跳，服务器回 `pong` |

### 3.3 服务端 → 客户端帧

| 帧 | 说明 |
|----|------|
| `{ "type":"ack", "op":"subscribe\|unsubscribe\|send", "channel":"…" }` | 操作确认 |
| `{ "type":"out", "channel":"http_debug", "content":"…", "seq":6, "time":"…" }` | **bot 出向消息实时推送**（speak 输出） |
| `{ "type":"pong" }` | ping 应答 |
| `{ "type":"error", "message":"…" }` | 帧解析失败 / 未知类型 |

---

## 4. 使用示例

### curl 调试对话（阻塞等回复）

```bash
curl -X POST http://localhost:9876/api/send \
  -H "Content-Type: application/json" \
  -d '{"content":"你好，用一句话回复我","user":"tester","displayName":"Tester"}'
# → {"ok":true,"burstSeen":true,"timedOut":false,"durationMs":10519,"replies":[{...}]}
```

### 增量拉取未读（客户端记住最后一条消息 id）

```bash
curl "http://localhost:9876/api/messages?channel=http_debug&afterId=30"
```

### 多对话线分区

```bash
curl -X POST http://localhost:9876/api/send \
  -d '{"channel":"http_room1","content":"分区对话","user":"alice"}'
```

### 鉴权

```bash
curl -X POST http://localhost:9876/api/send \
  -H "Authorization: Bearer <token>" \
  -d '{"content":"hi"}'
```

### WebSocket（Node 示例）

```js
const ws = new WebSocket("ws://localhost:9876/ws?channel=http_debug");
ws.onmessage = (e) => console.log("→", e.data);   // welcome / out / ack / pong
ws.onopen = () => {
  ws.send(JSON.stringify({ type: "send", channel: "http_debug", content: "hi" }));
  ws.send(JSON.stringify({ type: "ping" }));
};
```

---

## 5. 常见场景与注意事项

- **立即响应**：HTTP/WS 发消息默认 `isPrivate=true`（私聊视同提及），express 会话立即激活；不带则走权重节流，可能延迟或攒批。
- **命令消息**：被 `CommandSpawnCheck` 拦截的命令不会入库 → `burstSeen=false` 快速返回；未拦截的（如非命令前缀文本）会当普通对话处理并回复。
- **wait 阻塞时长**：express 会话单次回复通常 5–10s；多轮工具调用更久。建议 `timeoutMs` 按场景设置，避免默认 30s 过久。
- **并发**：同一频道并发多个 `wait=true` 请求，bot 回复可能被多个请求都观测到（无严格归属），调试场景可接受。
- **出向 id**：`platformMessageId` 形如 `http_00000001`（适配器自增 seq），作为该条 bot 消息在消息历史里的标识。
- **路由归属**：多适配器共存（如 `--test` 下 file + http）时，`http_` 前缀/白名单频道出向归本适配器；其他频道归各自适配器。
