# 路由契约（02）

> 前置：`00-decisions.md` D13–D23。本文定义频道 × 方向的源优先级、健康切换、强查与出向台账。

---

## 1. 配置模型

```sql
ChannelRoutes(ChannelId, Direction, AdapterId, Priority, Enabled)
-- Direction: In | Out
-- Priority: > 0 参与竞争（数值大者优先）；<= 0 仅归档
```

- 键是 `(ChannelId, AdapterId, Direction)`：同一适配器可在 A 频道为首选、B 频道为备选。
- 排序确定性：`Priority DESC → HealthState → AdapterId ASC`。
- 同一频道两个方向独立配置（入向偏稳、出向偏快是常见需求）。

---

## 2. 入向语义

```
事件到达（任意源）→ 一律入库（各自适配器库）
  ├─ 来源是该频道 In 方向最高优先级的可用源 → 入库 + 进入条件判定（可激活）
  ├─ 其他源（优先级更低 / <=0）            → 入库 + RouteRole=backup|orphan，不激活
  └─ 最高优先级源不可用 → 按优先级提升下一个可用源为 effective primary（写 route-switch 审计）
```

**为什么次要源也要入库**：主源可能静默丢消息（半死连接、重连丢窗口）。备源是"另一路真相"，是强查对账的依据。丢弃归档等于放弃可恢复性。

`RouteRole` 取值：`primary | backup | repaired | orphan`，供查询标注与对账使用。

---

## 3. 出向语义

- 每次发送实时选源（不缓存绑定），但带 **sticky 窗口**：同一 burst 内或 60s 内不换源，防止一次多段回复被劈到两个账号。
- 多适配器同平台时，用 `Channels.AdapterId` + `ChannelRoutes` 决定唯一发送者；不再靠 channel 名字前缀猜归属。
- **跨账号 fallback 默认关闭**（`OutboundAllowCrossAccount=false`）；仅同账号多适配器的自动切换默认开启。
- 发送结束写 `OutboundLedger`，并记录实际发送源。

---

## 4. 健康与切换

```csharp
record AdapterHealth(string AdapterId, RouteState State, DateTime LastOkAt,
                     int ConsecutiveFailures, double SendFailRate, long LagMs);
enum RouteState { Ok, Degraded, Down, Disabled }
```

可用条件：`State != Down && now - LastOkAt < LagThreshold && SendFailRate < X && Config.Enabled`。

- **迟滞**：连续 N 次失败（默认 3）或持续 M 秒（默认 30s）不可用 → 降级；连续 K 次成功（默认 3）→ 恢复。
- 全部候选不可用时：入向退化为"只归档不激活"，出向返回明确失败——**不降级为 `Priority <= 0` 的源**。

---

## 5. 强查（防丢段）

强查 = 查**适配器自己的消息库**，不是调用平台 API。

```
IAdapterQuery.QueryMessagesAsync(AdapterMessageQuery)  // 执行在适配器库里
AdapterMessageQuery { PlatformChannelId, TimeFrom, TimeTo, PlatformMessageIds?, Keyword?, Limit }
```

### 缺口检测

| 手段 | 说明 |
|---|---|
平台强查（首选） | 向缺口所在适配器库查区间；依赖 `CursorCapability`（`MonotonicId` 可按 id 区间，`TimeRange` 只能按时间+预期条数） |
跨源对账 | 两源归档按 `DedupeKey`/时间线对齐，找"A 有 B 无""B 有 A 无" |
序列自检 | 启动时与每 N 分钟：`max(localId)` 跳变、相邻时间差超阈值、条数突降 |

### 缺口分类

| Kind | 含义 | 处理 |
|---|---|---|
`Unconsumed` | 库里有，管道没消费/没合并（最常见） | 补消费，成本最低 |
`MissingInAdapter` | 该适配器当时没收到 | 尝试跨源补齐 |
`Unrecoverable` | 多源都没有 | 标记 + 在会话上下文插入"此处可能缺失"提示 |

补洞必须幂等（`UNIQUE(PlatformChannelId, PlatformMessageId)`）；补入行 `RouteRole=repaired`，`IngestState=repaired`，**不触发激活**。

### 平台探测（可选，默认关）

`IAdapterPlatformProbe`：仅当需要"服务端留档"时开启，带预算（默认每频道每小时 ≤ 4 次）与退避。

---

## 6. 出向分段台账

```sql
OutboundLedger(Id, ChannelId, AdapterId, SegmentIndex, SegmentCount,
               ContentHash, Status, SentAt, PlatformMessageId, ReplyTo, AttemptId)
-- Status: Pending | Sent | Failed | Skipped
```

- 发送前写入 `SegmentCount` 行 `Pending`；每段成功回填。
- 崩溃恢复：扫 `Pending/Failed` → 用 `ContentHash` + 目标频道近期消息比对，**续发剩余段**，不整条重发。
- 对应现状：`SpeakTool` 按空行分条逐条发送、`SendFileTool` fire-and-forget，均无台账。

---

## 7. 启动自愈流程

```
启动
 → 读各适配器游标 + OutboundLedger + ParkQueue
 → 对有 primary/backup 的频道各跑一次 audit（默认最近 15 分钟窗口）
     ① 适配器库强查 → 与本地归档比对 → 生成 MessageGap
     ② 补洞（repaired，不激活）
     ③ OutboundLedger 有 Pending → 校验后续发
 → 输出"启动自愈报告"进频道流水 + WebUI 概览
```

---

## 8. 可观测

```
query_resources        // 资源持有者 / 等待队列 / 最近丢弃原因
GET /v1/channel/{ref}/routes     // 两个有序列表 + 当前生效源 + 健康灯
PUT /v1/channel/{ref}/routes
POST /v1/channel/{ref}/audit     // 触发强查对账（异步，返回 taskId）
GET /v1/channel/{ref}/gaps
GET /v1/channel/{ref}/outbound
```

审计表：`ResourceAudit`、`SinkAudit`、`route-switch` 记录、`MessageGap` 状态变更。
