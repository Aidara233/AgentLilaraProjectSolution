# 日志系统重设计

## 目标

替换现有的框架日志+模型日志二分体系，建立基于信号追踪的结构化日志系统。解决以下痛点：

1. 无法实时观察执行进度（模型卡住时不知道卡在哪）
2. 无法区分上游问题还是程序问题
3. 日志分组过粗，难以专注分析
4. WebUI 展示原始 JSON，不可读
5. 信息不完整，缺少中间步骤

## 核心概念

- **Signal（信号）** — 因果链的根源标识。任何外部输入（消息到达、定时器触发、WebUI 操作）或内部决策（冲动触发主动发言）产生一个信号，沿处理链传播。信号可分支（多个频道检查同一条消息）、可级联（委托唤醒另一个频道）。
- **Scope（作用域）** — 执行者标识。`adapter:{platform}`、`channel:{channelId}`、`system`、`agent:{name}`、`webui`。
- **Branch（分支）** — 同一信号在同一 scope 内的多次进入的区分标识。值 = 该处理链根事件的数据库 PK。
- **Span（跨度）** — 有时长的操作，由 open + close 事件对表示。open 未配对 close = 当前正在执行或异常。
- **Event（事件）** — 时间点日志，三种类型：`open`（开始操作）、`close`（结束操作）、`event`（瞬时记录）。

## 存储

### 独立 SQLite 数据库

与主库分离，文件名 `logs.db`，放在现有 Storage 目录下。WAL 模式。

理由：日志高写入、可丢弃，生命周期与业务数据完全不同，可独立清理/重建。

### 单表设计

```sql
CREATE TABLE events (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    signal_id   TEXT NOT NULL,
    scope       TEXT NOT NULL,
    branch      INTEGER NOT NULL,
    parent_id   INTEGER,              -- 父操作的 open 事件 ID
    span_id     TEXT,                 -- open/close 配对标识，event 类型可为 NULL
    group_name  TEXT NOT NULL,        -- Engine/Model/Tool/Memory/Adapter/Plugin
    level       INTEGER NOT NULL,     -- 0=Debug 1=Info 2=Warn 3=Error
    type        TEXT NOT NULL,        -- 'open', 'close', 'event'
    timestamp   INTEGER NOT NULL,     -- unix milliseconds
    name        TEXT NOT NULL,        -- 操作名或事件摘要
    detail      TEXT                  -- JSON，结构化详情
);

CREATE INDEX idx_events_signal ON events(signal_id);
CREATE INDEX idx_events_scope_time ON events(scope, timestamp);
CREATE INDEX idx_events_branch ON events(branch);
CREATE INDEX idx_events_span ON events(span_id);
CREATE INDEX idx_events_group_time ON events(group_name, timestamp);
CREATE INDEX idx_events_level_time ON events(level, timestamp);
CREATE INDEX idx_events_open_unmatched ON events(span_id, type) WHERE type = 'open';
```

### 分组（group_name）

| 分组 | 覆盖内容 |
|------|----------|
| Engine | 循环调度、频道唤醒/休眠、闸门决策、委托 |
| Model | API 请求发出、流式进度、完成/失败/超时 |
| Tool | 工具调用、参数、返回值、耗时 |
| Memory | 检索、存储、embedding、向量搜索 |
| Adapter | 平台消息收发、协议细节 |
| Plugin | 插件加载/卸载、Provider 注册、热重载 |

## 信号传播机制

### 信号生成

| 触发场景 | 信号发生器 | signal_id 生成点 |
|---|---|---|
| 收到消息 | Adapter | 适配器解析完消息时 |
| 定时唤醒 | Engine | 定时器触发时 |
| 冲动值触发主动发言 | Engine | 冲动系统决策时 |
| WebUI 手动操作 | WebUI | 用户点击按钮时 |

### 信号传播规则

1. 信号沿因果链传播：适配器 → 消息总线 → 频道检查 → 闸门 → 模型调用 → 工具 → ...
2. 信号可分支：同一消息被多个频道检查，各自产生独立的 scope 记录
3. 信号可级联：委托唤醒另一个频道时，被唤醒方继承同一个 signal_id
4. 信号可被吸收：频道正在处理 S2 时收到 S3，S3 被标记为 `absorbed_by: S2`

### 信号吸收

当频道正在处理信号 S2 时，新信号 S3 到达且被纳入当前处理：

- S3 的轨迹记录：`adapter ● 收到 → channel ● absorbed_by S2`（S3 轨迹结束）
- S2 的轨迹记录：`● 吸收信号 S3`（detail 含 S3 的 signal_id、来源、内容摘要）
- 双向引用，UI 可自动拼接关联链

### 被忽略的信号

闸门未开启的信号同样记录完整轨迹（收到 → 评估 → ignored），证明"消息确实到了，是被主动忽略的"。

## Parent 追踪（AsyncLocal）

### 同一异步流内：自动传播

```csharp
public class SignalContext
{
    private static readonly AsyncLocal<SignalContext?> _current = new();
    public static SignalContext? Current => _current.Value;

    public string SignalId { get; init; }
    public string Scope { get; init; }
    public long Branch { get; private set; }
    public long? CurrentSpanId { get; private set; }

    public static SignalContext Enter(string signalId, string scope, string group, string name, long? parentSpanId = null)
    {
        var ctx = new SignalContext
        {
            SignalId = signalId,
            Scope = scope,
        };
        _current.Value = ctx;
        var rootId = ctx.Open(group, name, parentSpanId);
        ctx.Branch = rootId;
        return ctx;
    }

    public long Open(string group, string name, long? explicitParent = null)
    {
        var parentId = explicitParent ?? CurrentSpanId;
        var eventId = LogWriter.Enqueue(new LogEvent
        {
            SignalId = SignalId,
            Scope = Scope,
            Branch = Branch,
            ParentId = parentId,
            SpanId = GenerateSpanId(),
            Group = group,
            Type = "open",
            Name = name,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        CurrentSpanId = eventId;
        return eventId;
    }

    public void Close(string group, string name, object? detail = null)
    {
        LogWriter.Enqueue(new LogEvent
        {
            SignalId = SignalId,
            Scope = Scope,
            Branch = Branch,
            ParentId = CurrentSpanId, // close 的 parent 指向对应的 open
            SpanId = /* 与对应 open 相同 */,
            Group = group,
            Type = "close",
            Name = name,
            Detail = detail,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        // 恢复 CurrentSpanId 到父级
    }

    public void Event(string group, string name, int level = 1, object? detail = null)
    {
        LogWriter.Enqueue(new LogEvent
        {
            SignalId = SignalId,
            Scope = Scope,
            Branch = Branch,
            ParentId = CurrentSpanId,
            SpanId = null,
            Group = group,
            Type = "event",
            Name = name,
            Level = level,
            Detail = detail,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }
}
```

### 跨异步边界：显式传递

```csharp
// 频道 A 委托唤醒频道 B
var handoff = new SignalHandoff
{
    SignalId = SignalContext.Current.SignalId,
    ParentSpanId = SignalContext.Current.CurrentSpanId
};
channelB.Wake(handoff);

// 频道 B 收到后
var ctx = SignalContext.Enter(handoff.SignalId, $"channel:{channelB.Id}", "Engine", "delegated_turn", handoff.ParentSpanId);
```

## 开发者 API

### 设计原则

- 调用者只需提供"记录什么"，不需要管理 signal_id、scope、branch、parent 等元数据
- 所有上下文信息由 SignalContext（AsyncLocal）自动提供
- 最简调用：一个方法 + 要记录的内容

### 使用方式

```csharp
// 开始一个操作（自动从 AsyncLocal 获取上下文）
using (Signal.Open("Model", "模型调用", new { model = "claude-opus", tokens_in = 1800 }))
{
    // 中间记录事件
    Signal.Event("Model", "首token到达");
    Signal.Event("Model", "流式进行中", detail: new { tokens = 500 });

    // 操作结束时 Dispose 自动 close
}
// close 时可附加输出信息
using (var span = Signal.Open("Tool", "工具执行", new { tool = "web_search", args = query }))
{
    var result = await ExecuteTool();
    span.SetCloseDetail(new { result_summary = result.Summary, elapsed_ms = sw.ElapsedMilliseconds });
}

// 纯事件（无跨度）
Signal.Event("Engine", "闸门评估", detail: new { impulse = 0.82, threshold = 0.3 });
Signal.Warn("Adapter", "消息发送超时", detail: new { target = channelId, elapsed = 5000 });
Signal.Error("Model", "API 返回 500", detail: new { status = 500, body = errorBody });
```

### 信号入口点

```csharp
// 适配器收到消息时
using (Signal.Begin("Adapter", $"adapter:{platform}", "消息接收", new { sender, channel }))
{
    // 分发到总线...
}

// 频道循环被唤醒时
using (Signal.Continue(handoff, $"channel:{id}", "Engine", "频道处理"))
{
    // 闸门评估、模型调用...
}

// 主动发言（新信号）
using (Signal.Begin("Engine", $"channel:{id}", "主动发言", new { impulse = 0.9 }))
{
    // ...
}
```

## Open/Close 内容规范

### 各分组的 detail 字段

| group | open.detail | close.detail |
|---|---|---|
| Model | model, messages_count, temperature, max_tokens | tokens_out, elapsed_ms, finish_reason, tool_calls_count, error |
| Tool | tool_name, args | result_summary, elapsed_ms, success, error |
| Memory | query, strategy, top_k | matched_count, elapsed_ms |
| Engine/Gate | impulse, threshold, trigger, channel_id | result(open/ignore/absorbed), reason |
| Engine/Turn | channel_id, trigger_signal, message_summary | status(completed/suspended/error), rounds |
| Adapter/Send | target, message_type, content_summary | success, error, elapsed_ms |
| Plugin | plugin_name, action(load/unload/reload) | success, error, types_discovered |

## 写入层

### 架构

```
调用者 → Channel<LogEvent> (有界队列, 容量 10000)
              ↓
       后台单线程消费者
              ↓
    ┌─────────┴─────────┐
    ↓                   ↓
 SQLite 批量写入     推送 WebUI 订阅者
 (每100ms或50条)     (同批次，保证顺序)
```

### 实现要点

- `System.Threading.Channels.Channel<LogEvent>` 有界队列
- 后台 Task 循环读取，攒批后 BEGIN/INSERT.../COMMIT 一次性写入
- 同一批次写完后通知 WebUI 订阅者（IDataSource.Subscribe 回调）
- 内存维护 `ConcurrentDictionary<string, SpanInfo>` 跟踪当前 open 的 span（快速查"卡在哪"）
- 队列满时丢弃最旧条目 + 写一条 Warn 标记丢失
- 进程崩溃最多丢失最后一批未刷盘数据（~100ms），可接受

### Open Span 内存追踪

```csharp
// 快速查询"当前卡在哪"，不走数据库
ConcurrentDictionary<string, OpenSpanInfo> _openSpans;

// open 时加入
_openSpans[spanId] = new OpenSpanInfo { ... };

// close 时移除
_openSpans.TryRemove(spanId, out _);

// WebUI 查询
public IEnumerable<OpenSpanInfo> GetCurrentlyRunning() => _openSpans.Values;
```

## WebUI 展示

### 可视化方案：Git-Tree 风格

纵轴 = 时间（每行一个事件），横轴 = scope 泳道。竖线表示 span 存活期间，节点表示事件。

```
time       adapter-qq    channel-B
           │
12:00:01   ● msg_recv    │
           │─────────────→● turn_start
           │             │
12:00:01   │             ○ 闸门开启
           │             │ ○ 记忆检索开启
           │             │ │
           │             │ ○ 记忆检索结束
           │             │
           │             │ ○ 模型调用开启
           │             │ │
           │             │ │ ● 首token到达
           │             │ │ ● 流式 500 tokens
           │             │ │
           │             │ ○ 模型调用结束
           │             │
           │             ○ 闸门关闭
```

### 渲染规则

- **○ (open/close)** — 开始/结束一条竖线
- **● (event)** — 当前线上的点，不产生新线
- **横线/斜线** — 跨 scope 因果传递
- **节点右侧** — 事件名称 + 简要信息
- **竖线** — span 存活期间（有操作在进行中）

### 布局

```
┌─ 筛选栏（级别、分组、scope 黑白名单）──────────────────────┐
├──────────── 左：信号树 ─────────────┬── 右：详情面板 ───────┤
│  时间轴 + 泳道 + 节点              │  选中节点的完整信息    │
│  （可滚动、可缩放）                │  open+close 合并展示  │
│                                    │  关联信号链接         │
└────────────────────────────────────┴──────────────────────┘
```

### 筛选与导航

- 级别过滤：Debug/Info/Warn/Error 复选框
- 分组过滤：Engine/Model/Tool/Memory/Adapter/Plugin 复选框
- Scope 黑白名单：指定只看/不看某些 scope
- 信号列表：默认显示最近 N 个信号，点击进入某信号的完整树
- 泳道排序：活跃度高的靠左，不活跃的需横向滚动
- 实时模式：新事件自动追加，无需手动刷新

### 详情面板

点击/悬停节点时，右侧显示：
- 事件基本信息（时间、group、level、scope）
- 如果是 span（有 span_id），自动合并 open + close 的 detail 对照展示
- 关联信号链接（absorbed_by / absorbs）
- 耗时计算（close.timestamp - open.timestamp）

### 信号追踪视图

查看某个信号时，如果该信号被吸收（absorbed_by），自动拼接目标信号从吸收点之后的事件：

```
── S3 的追踪视图 ──────────────────
adapter  ● 收到消息
         │──→ channel-B ● 到达，被吸收 → S2

         ┄┄┄ 以下为 S2 中 S3 介入后的部分 ┄┄┄

channel-B │ ● 吸收信号 S3
          │ ○ 模型调用开启
          │ │  (卡住)
```

## 接入点

### 启动阶段

| 位置 | 记录内容 |
|---|---|
| Program.cs | 程序启动、配置加载、服务注册 |
| MasterEngine 启动 | 各子系统启动指令发出 |
| 各子系统 | 汇报启动/加载/就绪状态 |
| PluginLoader | 插件发现、加载、注册 |

### 运行阶段

| 位置 | 记录内容 |
|---|---|
| Adapter 收消息 | 信号生成，消息解析 |
| Adapter 发消息 | 发送目标、结果、耗时 |
| 消息总线写入 | 信号分发 |
| 频道检查 | 是否认领、闸门评估 |
| 频道循环 | 上下文组装、模型调用、工具执行、挂起/完成 |
| 模型 API | 请求发出、首 token、流式进度、完成/超时/错误码 |
| 工具执行 | 调用参数、返回值、耗时 |
| 记忆操作 | 检索策略、命中数、存储 |
| 委托 | 发起、接受/拒绝、执行、结果回传 |
| 睡眠/唤醒 | 状态切换、梦话触发 |

### 关闭阶段

| 位置 | 记录内容 |
|---|---|
| 优雅停机 | 各子系统关闭顺序和状态 |
| 异常退出 | 未捕获异常 + 堆栈 |

## 清理策略

- **触发时机**：程序启动时 + 进入睡眠（小睡/大睡）时
- **清理规则**：`DELETE FROM events WHERE timestamp < ?`（超过配置天数的记录）
- **告警**：条数或文件大小超阈值时写 Warn 日志
- **配置项**：保留天数（默认 7 天）、告警阈值（条数/MB）
- **API**：暴露查询/手动清理接口，WebUI 可触发手动清理

## 日志级别

| 级别 | 值 | 用途 |
|---|---|---|
| Debug | 0 | 详细内部状态，日常不看 |
| Info | 1 | 正常流程关键节点 |
| Warn | 2 | 可恢复的异常情况 |
| Error | 3 | 失败、需要关注 |

运行时可调（WebUI 或配置文件），低于设定级别的事件不写入队列（源头过滤，零开销）。

## 与现有系统的关系

### 完全替换（连根拔起）

- 现有框架日志（FrameworkLogger）→ 移除，统一进 events 表
- 现有模型日志（ModelCallLog 表 + Repository）→ 移除，由新系统的 Token 聚合表替代
- 旧日志相关 WebUI 页面 → 移除，后续统一重建

### Token 聚合表（派生产物）

日志写入线程处理 Model close 事件时，顺带写入轻量统计表：

```sql
CREATE TABLE token_usage (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp   INTEGER NOT NULL,
    model       TEXT NOT NULL,
    caller_tag  TEXT,           -- Channel:{id} / System / SubAgent:{id} / Review:{mode}
    tokens_in   INTEGER NOT NULL,
    tokens_out  INTEGER NOT NULL,
    cached_in   INTEGER DEFAULT 0,
    elapsed_ms  INTEGER,
    success     INTEGER DEFAULT 1
);

CREATE INDEX idx_token_usage_time ON token_usage(timestamp);
CREATE INDEX idx_token_usage_model ON token_usage(model, timestamp);
CREATE INDEX idx_token_usage_caller ON token_usage(caller_tag, timestamp);
```

- 保留期独立于 events 表（默认 90 天）
- 不是独立日志系统，是 events 的聚合视图
- WebUI Token 统计页查此表

### 迁移路径

1. 新建日志基础设施（LogWriter、SignalContext、数据库、token_usage 表）✅
2. 在关键路径埋点（适配器、频道循环、模型调用）✅
3. FrameworkLogger 改为兼容层（底层转发 Signal，保留文件写入供旧 WebUI 页面使用）✅
4. 日志 WebUI 页面后续与卡片系统 Phase 3 一并设计

### 遗留债务（待 WebUI Phase 3 时清理）

以下文件/调用在 WebUI 日志页迁移完成后应移除：

| 文件 | 说明 |
|------|------|
| `Engine/Core/FrameworkLogger.cs` | 兼容层，25 个文件 170+ 处调用。迁移后删除 |
| `Database/ModelCallLog.cs` | 旧 token 统计实体。新系统用 token_usage 表替代 |
| `Database/ModelCallLogRepository.cs` | 旧 token 统计仓库 |
| `WebUI/Services/LogStreamService.cs` | 旧实时日志推送。新系统用 LogWriter.Subscribe 替代 |
| `WebUI/Services/TokenStatsService.cs` | 旧 token 统计服务 |
| `WebUI/Components/Pages/Logs.razor` | 旧实时日志页 |
| `WebUI/Components/Pages/Logs_Model.razor` | 旧模型日志页 |
| `WebUI/Components/Pages/Logs_Tokens.razor` | 旧 token 统计页 |
| `CoreBase.LogOutput()` + `CallLogRepo` | 旧模型 JSON 文件写入 + DB 写入 |

清理时机：WebUI 日志 Provider 实现后，一次性移除上述所有文件和调用。

