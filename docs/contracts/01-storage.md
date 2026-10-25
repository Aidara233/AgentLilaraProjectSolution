# 存储契约（01）

> 前置：`00-decisions.md` D5–D12。本文定义物理布局、最小表契约、写入路径与迁移规则。

---

## 1. 物理布局

```
Storage/Data/
  kernel.db                    频道注册表与绑定、路由、规则/图、命中与资源审计、会话元数据、
                               提案、设置、快照、schema 版本
  adapters/
    onebot-main.db             适配器私有：消息归档 + FTS + 索引
    icalingua-main.db
    http.db
    file.db
  sources/
    email-imap.db              流源私有：事件归档 + 索引
    ha-livingroom.db
  spool/                       写失败重放（JSONL，按库名分文件）
```

分库原则：**数据所有权随适配器/流源**；`kernel.db` 只放跨源共享的元数据。SQLite 写锁按库隔离，因此适配器之间天然并行，不需要引入外部数据库。

---

## 2. 最小归档表契约（框架要求，适配器不得删改列）

```sql
CREATE TABLE Messages(
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  PlatformChannelId TEXT NOT NULL,   -- "group_616565017" / "private_1664093638" / 源私有键
  PlatformMessageId TEXT,            -- 平台侧 id（可能为空：Console/File）
  SenderPlatformId TEXT,
  SenderDisplayName TEXT,
  Kind TEXT,                         -- text | image | file | system | notice
  Text TEXT,
  RawRef TEXT,                       -- RawPayload=external 时指向 payload 表/文件
  Time INTEGER,                      -- UTC ticks
  IngestedUtc INTEGER,
  DedupeKey TEXT,
  IsFromBot INTEGER,
  ReplyToPlatformMessageId TEXT,
  RouteRole TEXT                     -- primary | backup | repaired | orphan
);
CREATE UNIQUE INDEX UX_Msg ON Messages(PlatformChannelId, PlatformMessageId);
CREATE INDEX IX_ChanTime ON Messages(PlatformChannelId, Time);
CREATE INDEX IX_Time ON Messages(Time);
CREATE INDEX IX_Dedupe ON Messages(DedupeKey);
```

适配器可追加列/表/索引；不得改动上述列名与语义。

`RawPayload` 三档：

| 档 | 行为 |
|---|---|
`full` | 原文存 `Payloads` 表（或列） |
`textOnly` | 不存原文 |
`external`（默认） | ≤8KB 存原文；更大（合并转发、长文）落 `Payloads` 或文件，`RawRef` 指向 |

FTS5（可选，阶段 B 末引入）：`MessagesFts(Text, SenderDisplayName)`，与 `Messages` 用 rowid 关联。

---

## 3. kernel.db 关键表

```sql
Channels(Id, AdapterId, PlatformChannelId, Kind, Alias, Status, ActivationPolicy,
         BoundAt, CreatedAt, CreatedBy)
ChannelRoutes(ChannelId, Direction, AdapterId, Priority, Enabled)   -- Direction: In|Out
MessageGap(Id, ChannelId, AdapterId, FromTime, ToTime, FromPlatformId, ToPlatformId,
           DetectedBy, Kind, Status, RepairedAt)
OutboundLedger(Id, ChannelId, AdapterId, SegmentIndex, SegmentCount,
               ContentHash, Status, SentAt, PlatformMessageId, ReplyTo, AttemptId)
ResourceAudit(Id, Seq, ResourceId, AttemptId, WaitMs, Outcome, WaitedMs, At)
SinkAudit(Id, Seq, GraphId, SinkId, Outcome, At)
NodeAudit(Id, Seq, GraphId, NetworkId, BlockId, Phase, Message, At)
Signals(Id, PortId, PayloadJson, PayloadHash, Delivery, EmittedAt, ConsumedBy, ConsumedAt)
SignalCursor(PortId, LastSeq, UpdatedAt)          -- AtLeastOnce 端口的投递游标
SessionAudit(Id, SessionId, ChangedKey, BeforeJson, AfterJson, At)
InstanceVariable(GraphId, Scope, Name, Type, ValueJson, UpdatedAt)   -- 图实例变量
VariableAudit(Id, Seq, GraphId, Scope, Name, Old, New, At)          -- 变量变更流水
ParkQueue(Id, GraphId, SpliceId, Scope, PayloadJson, ReleaseAt, ResourceRef, AttemptId, CreatedAt)
Proposals(Id, Kind, DiffJson, Rationale, Status, CreatedAt, DecidedAt, DecidedBy)
AuditLog(Id, Seq, Actor, Action, Target, BeforeHash, AfterHash, At)
Snapshots(Id, Scope, ContentHash, Path, CreatedAt)
SchemaVersion(DbId, Version, AppliedAt)
```

`Priority`：`> 0` 参与竞争，数值大者优先；`<= 0` 仅归档、不参与取用与发送。

---

## 4. 写入路径

```
Ingest ─▶ BoundedChannel<IncomingRecord>（容量 N；满则按源配置 Block 或 DropOldest）
      ─▶ 每库一个 AdapterWriter（单写者）
           批：达到 BatchSize 条 / 每 FlushIntervalMs / 收到 Flush 信号
           事务：BEGIN → INSERT 批量 → 更新 FTS → 记录 maxLocalId → COMMIT
           失败：重试 3 次（指数退避）→ 落 spool/{db}.jsonl → 后台重放
```

默认参数（可配）：`BatchSize=200`、`FlushIntervalMs=500`、`ChannelCapacity=10000`、`DurabilityMode=buffered`。

优雅退出与 `--test` 收尾必须 `FlushAll()`，否则断言抖动。

---

## 5. 查询可见性：PendingIndex

已入缓冲未落盘的消息必须可被查询看到。

- 每适配器维护 `PendingIndex`（内存环形，容量 = `ChannelCapacity`），元素为 `(PlatformChannelId, localId?, Time, SenderPlatformId, Text, DedupeKey)`。
- 查询先读库，再合并 `PendingIndex`，统一排序后分页；`localId` 未分配时返回临时引用 `pending:{n}`。
- `Flush` 成功后从索引移除；崩溃后索引直接丢弃（数据仍在 spool 或已落库）。

---

## 6. 跨库查询与去重

```
QueryMessages(q)：
  1. channelRef → [(adapterId, platformChannelId) …]      // 可能多个（多源同频道）
  2. 并行只读连接各库执行 SQL（时间/关键词/发送者/附件过滤）
  3. 合并 PendingIndex
  4. 按 Time 排序
  5. 跨库按 DedupeKey 去重（仅在 len(normalize(Text)) >= 6 或含附件哈希时）
  6. 分页返回，并附 sources:[{adapterId, hits}]
```

去重保留策略：按 `ChannelRoutes.Priority` 高者保留，另一条记 `suppressed-by-dedupe` 到审计，可查。

---

## 7. 迁移

- 每个库有 `SchemaVersion(DbId, Version)`；迁移脚本逐库执行并落 `AppliedAt`。
- 列新增沿用现有 `ALTER TABLE ... ADD COLUMN` 惯例（参考 `DbManager` 现有迁移路径）。
- 多库无单事务：迁移失败时记录已完成的库，重试幂等。
- 备份：先 `PRAGMA wal_checkpoint(TRUNCATE)` 再复制文件；`kernel.db` 与各库分别备份，恢复时以 `SchemaVersion` 对齐。

---

## 8. 待定项

| 项 | 说明 |
|---|---|
FTS5 引入时机 | 阶段 B 末或归档量到阈值后 |
冷归档 | 仅提供手动/可选，不做强制删除 |
`Payloads` 表 vs 文件 | 优先表内 `BLOB`，>1MB 走文件 |
