# 事件与事实契约（03）

> 前置：`00-decisions.md` D1–D4、D24–D28；`04-graph-model.md`。本文定义入站事件的形状、事实命名空间与 tick 身份。

---

## 1. 入站事件

```csharp
public sealed record InboundEvent {
    string EventId;                      // GUID
    long   Seq;                          // 全局单调递增，决定寄存器/审计提交顺序
    SourceRef Source;                    // (AdapterId|StreamId|SignalPortId, Kind)
    string? ChannelRef;                  // 已绑定频道才填；未绑定为 null
    string? PlatformChannelId;
    string? PlatformMessageId;
    string  SenderPlatformId;
    string? SenderDisplayName;
    string  Kind;                        // text | image | file | system | notice
    string  Text;
    string? RawRef;                      // 原始载荷引用（external 档）
    DateTime Time;
    string? ReplyToPlatformMessageId;
    string? SystemEventSubtype;
    bool    IsFromBot;
    string? CausedBy;                    // 上游事件 id（防环/追踪）
    int     Depth;                       // 因果深度
    string? OriginRuleId;                // 由哪条规则/图产生（信号反射抑制）
}
```

**规范化落在适配器**：适配器负责把平台载荷转成 `InboundEvent`，并保留原始载荷（`RawRef`）。框架只消费规范化后的形状。

---

## 2. 事实（Facts）命名空间

`FactsBag` 为扁平的、大小写不敏感的键值表，值限 `string / number / bool / string[]`。

| 前缀 | 来源 | 示例 |
|---|---|---|
`msg.*` | 事件本体 | `msg.text` `msg.length` `msg.isMention` `msg.isPrivate` `msg.replyToBot` `msg.systemEvent` |
`sender.*` | 用户/人物 | `sender.platformId` `sender.displayName` `sender.trust` `sender.permission` |
`channel.*` | 频道 | `channel.id` `channel.name` `channel.recentCount` `channel.silenceSec` |
`attachment.*` | 附件 | `attachment.count` `attachment.types[]` |
`source.*` | 源 | `source.id` `source.kind` `source.adapterId` |
`time.*` | 时间（本地） | `time.local` `time.weekday` `time.hour` |
`tick.*` | 本拍身份 | `tick.source` `tick.seq` `tick.time` `tick.traceId` `tick.segment` |
`sig.*` | 信号载荷 | `sig.state` `sig.consecutiveFailures` |
`ext.*` | 源扩展字段 | `ext.ha.temperature` `ext.mail.subject` `ext.mail.from` |
`sem.*` | 异步语义（可选） | `sem.aiRelated` `sem.urgency` |

### 缺失语义

- **图内"未触发源"**：一律给块的默认值（常量），不使用"未知"，见 `04-graph-model.md` §3。
- **外部键缺失**（`ext.*` / `sig.*`）：保留三值 `unknown`；`NOT(unknown)=unknown`；只有最终 `true` 才放行。
- 规则里引用不存在的键 → 该谓词 `unknown`，试跑面板以第三种颜色标出。

---

## 3. Tick 身份

- 一次入站事件（或一次 Park 放行、自唤醒）= 一个 tick。
- 同一物理消息触发多个图（如频道图 + 适配器图）时，按"每事件一个 tick"生成**多个独立计算进程**，各自读状态快照、互不等待。
- **提交按 `Seq` 排序**：图实例变量与模块状态的写入以 `Seq` 定序，与线程调度无关，保证重放一致。
- `tick.traceId` / `tick.segment` 标识本次激活的因果链与段序：续延放行时 `traceId` 不变、`segment + 1`；派生信号另起 `traceId` 并记 `parentTraceId`。

---

## 4. 并发下的一致性

| 情形 | 规则 |
|---|---|
两个 tick 写同一实例变量 | `LastWriteWins(Seq)`；关键变量可选 `CompareAndSet(expected)` 或 `Guard(expr)` |
两个 tick 竞争同一资源 | 由方法参数 `waitMs` + 资源自身仲裁处理；败者得 `resource.dropped.*` 信号 |
两个 tick 都对同一频道 `sink.session.start` | 资源仲裁决定唯一持有者，败者走 `waitMs` 语义（丢弃或排队） |
副作用执行 | 在状态提交之后执行；失败不回收状态，但写 `SinkAudit` |

---

## 5. 信号端口

```csharp
record SignalPort(string Id, SignalCategory Category, JsonNode PayloadSchema,
                  SignalDelivery Delivery, string Description);
enum SignalDelivery { FireAndForget, AtLeastOnce }
```

首批端口：

```
adapter.down                     AtLeastOnce     -- 适配器进入不可用
adapter.up                       AtLeastOnce     -- 适配器恢复
adapter.degraded                 AtLeastOnce     -- 降级（失败率/延迟超阈值）
adapter.health                   AtLeastOnce     -- 聚合入口：状态变化即发
adapter.error                    FireAndForget   -- 单次错误
channel.gap.detected             AtLeastOnce
channel.gap.repaired             AtLeastOnce
channel.routeSwitched            FireAndForget
resource.freed                   AtLeastOnce
resource.acquired                FireAndForget
resource.dropped.busy            FireAndForget
resource.dropped.timeout         FireAndForget
resource.dropped.queueFull       FireAndForget
resource.dropped.serialization   FireAndForget
session.finished                 AtLeastOnce
timer.tick                       FireAndForget
runtime.error                    AtLeastOnce     -- 模块执行失败（graphId/networkId/blockId/phase/message）
```

框架**不内置限流**：端口不设令牌桶、不做自动聚合。需要节流/合并就在信号图里用 `splice.throttle` / `splice.debounce` / `splice.counter` 自己拼——规则显式、可读、可改。

`AtLeastOnce` 端口落 `Signals` 表 + 投递游标，崩溃后可重放；接收端按 `(portId, payloadHash, window)` 幂等。

---

## 6. 防环

| 层 | 手段 |
|---|---|
配置期 | 图校验：环路默认拒绝保存，自环拒绝，扇出/最长路径告警 |
运行期 | 同一 trace 内重复进入同一续延实例 → 中止该 trace（精确判定）；`trace.maxSegments`（默认 32）兜底；派生信号另起 `traceId` 并记 `parentTraceId` |

信号由用户显式接线，因此不设全局"信号激活"默认关闭；保险丝只防手滑与风暴。
