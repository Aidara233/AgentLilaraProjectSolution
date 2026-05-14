# 日志系统重设计 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 建立基于信号追踪的结构化日志系统，替换现有 FrameworkLogger + ModelCallLog 双体系。

**Architecture:** 独立 SQLite 数据库（logs.db）+ 单表 events + AsyncLocal 上下文传播 + Channel\<T\> 缓冲写入 + token_usage 聚合派生表。

**Tech Stack:** .NET 8, System.Threading.Channels, Microsoft.Data.Sqlite, AsyncLocal\<T\>

**Scope:** 底层基础设施 + 开发者 API + 关键路径埋点 + 旧系统移除。不含 WebUI 日志页面（后续与卡片系统 Phase 3 合并）。

---

## 文件结构

### 新建

- `AgentLilara.PluginSDK/Logging/ISignalLogger.cs` — 通用日志写入接口（插件可用）
- `AgentLilara.PluginSDK/Logging/ILogAccess.cs` — 高级读写接口（按需注入）
- `AgentLilara.PluginSDK/Logging/LogEventInfo.cs` — SDK 层数据传输类型
- `AgentCoreProcessor/Logging/LogDatabase.cs` — SQLite 连接管理、建表、清理
- `AgentCoreProcessor/Logging/LogEvent.cs` — 内部事件数据模型
- `AgentCoreProcessor/Logging/LogWriter.cs` — Channel\<T\> 队列 + 后台批量写入
- `AgentCoreProcessor/Logging/SignalContext.cs` — AsyncLocal 上下文、Open/Close/Event API
- `AgentCoreProcessor/Logging/Signal.cs` — 静态门面 API（开发者直接调用）
- `AgentCoreProcessor/Logging/TokenAggregator.cs` — 从 Model close 事件派生 token_usage
- `AgentCoreProcessor/Logging/OpenSpanTracker.cs` — 内存追踪当前 open 的 span
- `AgentCoreProcessor/Logging/ILogQuery.cs` — 内部查询接口
- `AgentCoreProcessor/Logging/LogQuery.cs` — 查询实现
- `AgentCoreProcessor/Logging/LogAccessImpl.cs` — SDK ILogAccess 的宿主实现

### 修改

- `AgentCoreProcessor/Engine/Core/MasterEngine.cs` — 初始化日志系统，移除 ModelCallLogRepository
- `AgentCoreProcessor/Core/CoreBase.cs` — 替换 LogOutput() 为 Signal API
- `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs` — 埋点信号入口 + 关键步骤
- `AgentCoreProcessor/Engine/System/SystemEngine.cs` — 埋点
- `AgentCoreProcessor/Adapter/AdapterManager.cs` — 信号生成入口
- `AgentCoreProcessor/Program.cs` — 注册日志服务
- `AgentCoreProcessor/Engine/Core/EventBus.cs` — 信号传递埋点

### 移除

- `AgentCoreProcessor/Engine/Core/FrameworkLogger.cs`
- `AgentCoreProcessor/Database/ModelCallLog.cs`
- `AgentCoreProcessor/Database/ModelCallLogRepository.cs`
- `AgentCoreProcessor/WebUI/Services/LogStreamService.cs`
- `AgentCoreProcessor/WebUI/Components/Pages/Logs.razor`
- `AgentCoreProcessor/WebUI/Components/Pages/Logs_Model.razor`
- `AgentCoreProcessor/WebUI/Components/Pages/Logs_Tokens.razor`

---

### Task 1: 数据模型与数据库

**Files:**
- Create: `AgentCoreProcessor/Logging/LogEvent.cs`
- Create: `AgentCoreProcessor/Logging/LogDatabase.cs`

- [ ] **Step 1: 创建 LogEvent 数据模型**

```csharp
// AgentCoreProcessor/Logging/LogEvent.cs
namespace AgentCoreProcessor.Logging;

public class LogEvent
{
    public long Id { get; set; }
    public string SignalId { get; set; } = "";
    public string Scope { get; set; } = "";
    public long Branch { get; set; }
    public long? ParentId { get; set; }
    public string? SpanId { get; set; }
    public string GroupName { get; set; } = "";
    public int Level { get; set; } = 1; // 0=Debug 1=Info 2=Warn 3=Error
    public string Type { get; set; } = "event"; // "open", "close", "event"
    public long Timestamp { get; set; }
    public string Name { get; set; } = "";
    public string? Detail { get; set; } // JSON
}

public static class LogLevel
{
    public const int Debug = 0;
    public const int Info = 1;
    public const int Warn = 2;
    public const int Error = 3;
}

public static class LogGroup
{
    public const string Engine = "Engine";
    public const string Model = "Model";
    public const string Tool = "Tool";
    public const string Memory = "Memory";
    public const string Adapter = "Adapter";
    public const string Plugin = "Plugin";
}
```

- [ ] **Step 2: 创建 LogDatabase**

```csharp
// AgentCoreProcessor/Logging/LogDatabase.cs
using Microsoft.Data.Sqlite;

namespace AgentCoreProcessor.Logging;

public class LogDatabase : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _dbPath;

    public LogDatabase(string storagePath)
    {
        _dbPath = Path.Combine(storagePath, "logs.db");
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS events (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                signal_id   TEXT NOT NULL,
                scope       TEXT NOT NULL,
                branch      INTEGER NOT NULL DEFAULT 0,
                parent_id   INTEGER,
                span_id     TEXT,
                group_name  TEXT NOT NULL,
                level       INTEGER NOT NULL DEFAULT 1,
                type        TEXT NOT NULL DEFAULT 'event',
                timestamp   INTEGER NOT NULL,
                name        TEXT NOT NULL,
                detail      TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_events_signal ON events(signal_id);
            CREATE INDEX IF NOT EXISTS idx_events_scope_time ON events(scope, timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_branch ON events(branch);
            CREATE INDEX IF NOT EXISTS idx_events_span ON events(span_id);
            CREATE INDEX IF NOT EXISTS idx_events_group_time ON events(group_name, timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_level_time ON events(level, timestamp);

            CREATE TABLE IF NOT EXISTS token_usage (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp   INTEGER NOT NULL,
                model       TEXT NOT NULL,
                caller_tag  TEXT,
                tokens_in   INTEGER NOT NULL,
                tokens_out  INTEGER NOT NULL,
                cached_in   INTEGER DEFAULT 0,
                elapsed_ms  INTEGER,
                success     INTEGER DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS idx_token_time ON token_usage(timestamp);
            CREATE INDEX IF NOT EXISTS idx_token_model ON token_usage(model, timestamp);
            CREATE INDEX IF NOT EXISTS idx_token_caller ON token_usage(caller_tag, timestamp);
            """;
        cmd.ExecuteNonQuery();
    }

    public SqliteConnection Connection => _conn;

    public void Cleanup(int retainDays, int tokenRetainDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retainDays).ToUnixTimeMilliseconds();
        var tokenCutoff = DateTimeOffset.UtcNow.AddDays(-tokenRetainDays).ToUnixTimeMilliseconds();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM events WHERE timestamp < @cutoff; DELETE FROM token_usage WHERE timestamp < @tokenCutoff;";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        cmd.Parameters.AddWithValue("@tokenCutoff", tokenCutoff);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn?.Dispose();
}
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build AgentCoreProcessor`
Expected: 成功（新文件无外部依赖冲突）

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/Logging/LogEvent.cs AgentCoreProcessor/Logging/LogDatabase.cs
git commit -m "feat(logging): add LogEvent model and LogDatabase with schema"
```

---

### Task 2: LogWriter 批量写入引擎

**Files:**
- Create: `AgentCoreProcessor/Logging/LogWriter.cs`
- Create: `AgentCoreProcessor/Logging/OpenSpanTracker.cs`
- Create: `AgentCoreProcessor/Logging/TokenAggregator.cs`

- [ ] **Step 1: 创建 LogWriter**

```csharp
// AgentCoreProcessor/Logging/LogWriter.cs
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace AgentCoreProcessor.Logging;

public class LogWriter : IDisposable
{
    private readonly Channel<LogEvent> _channel;
    private readonly LogDatabase _db;
    private readonly OpenSpanTracker _spanTracker;
    private readonly TokenAggregator _tokenAggregator;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumeTask;
    private readonly List<Action<IReadOnlyList<LogEvent>>> _subscribers = new();
    private readonly object _subLock = new();

    public LogWriter(LogDatabase db, OpenSpanTracker spanTracker, TokenAggregator tokenAggregator)
    {
        _db = db;
        _spanTracker = spanTracker;
        _tokenAggregator = tokenAggregator;
        _channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _consumeTask = Task.Run(ConsumeLoop);
    }

    public void Enqueue(LogEvent evt)
    {
        if (evt.Type == "open")
            _spanTracker.TrackOpen(evt);

        _channel.Writer.TryWrite(evt);
    }

    public void EnqueueClose(LogEvent evt)
    {
        _spanTracker.TrackClose(evt.SpanId!);
        _channel.Writer.TryWrite(evt);
    }

    public IDisposable Subscribe(Action<IReadOnlyList<LogEvent>> callback)
    {
        lock (_subLock) _subscribers.Add(callback);
        return new Unsubscriber(() => { lock (_subLock) _subscribers.Remove(callback); });
    }

    private async Task ConsumeLoop()
    {
        var batch = new List<LogEvent>(64);
        var reader = _channel.Reader;

        while (!_cts.Token.IsCancellationRequested)
        {
            batch.Clear();
            try
            {
                var evt = await reader.ReadAsync(_cts.Token);
                batch.Add(evt);

                var deadline = Environment.TickCount64 + 100; // 100ms window
                while (batch.Count < 50 && Environment.TickCount64 < deadline
                       && reader.TryRead(out var more))
                {
                    batch.Add(more);
                }

                WriteBatch(batch);
                NotifySubscribers(batch);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* 日志系统自身不能崩 */ }
        }

        // drain remaining
        while (reader.TryRead(out var remaining))
        {
            batch.Add(remaining);
            if (batch.Count >= 50) { WriteBatch(batch); batch.Clear(); }
        }
        if (batch.Count > 0) WriteBatch(batch);
    }

    private void WriteBatch(List<LogEvent> batch)
    {
        using var tx = _db.Connection.BeginTransaction();
        using var cmd = _db.Connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO events (signal_id, scope, branch, parent_id, span_id, group_name, level, type, timestamp, name, detail)
            VALUES (@sig, @scope, @branch, @parent, @span, @group, @level, @type, @ts, @name, @detail)
            """;

        var pSig = cmd.Parameters.Add("@sig", SqliteType.Text);
        var pScope = cmd.Parameters.Add("@scope", SqliteType.Text);
        var pBranch = cmd.Parameters.Add("@branch", SqliteType.Integer);
        var pParent = cmd.Parameters.Add("@parent", SqliteType.Integer);
        var pSpan = cmd.Parameters.Add("@span", SqliteType.Text);
        var pGroup = cmd.Parameters.Add("@group", SqliteType.Text);
        var pLevel = cmd.Parameters.Add("@level", SqliteType.Integer);
        var pType = cmd.Parameters.Add("@type", SqliteType.Text);
        var pTs = cmd.Parameters.Add("@ts", SqliteType.Integer);
        var pName = cmd.Parameters.Add("@name", SqliteType.Text);
        var pDetail = cmd.Parameters.Add("@detail", SqliteType.Text);

        foreach (var evt in batch)
        {
            pSig.Value = evt.SignalId;
            pScope.Value = evt.Scope;
            pBranch.Value = evt.Branch;
            pParent.Value = evt.ParentId.HasValue ? evt.ParentId.Value : DBNull.Value;
            pSpan.Value = evt.SpanId != null ? evt.SpanId : DBNull.Value;
            pGroup.Value = evt.GroupName;
            pLevel.Value = evt.Level;
            pType.Value = evt.Type;
            pTs.Value = evt.Timestamp;
            pName.Value = evt.Name;
            pDetail.Value = evt.Detail != null ? evt.Detail : DBNull.Value;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        _tokenAggregator.ProcessBatch(batch);
    }

    private void NotifySubscribers(List<LogEvent> batch)
    {
        List<Action<IReadOnlyList<LogEvent>>> subs;
        lock (_subLock) subs = _subscribers.ToList();
        foreach (var sub in subs)
        {
            try { sub(batch); } catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _channel.Writer.Complete();
        _consumeTask.Wait(TimeSpan.FromSeconds(3));
        _cts.Dispose();
    }

    private class Unsubscriber(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
```

- [ ] **Step 2: 创建 OpenSpanTracker**

```csharp
// AgentCoreProcessor/Logging/OpenSpanTracker.cs
using System.Collections.Concurrent;

namespace AgentCoreProcessor.Logging;

public class OpenSpanInfo
{
    public string SpanId { get; init; } = "";
    public string SignalId { get; init; } = "";
    public string Scope { get; init; } = "";
    public string GroupName { get; init; } = "";
    public string Name { get; init; } = "";
    public long StartedAt { get; init; }
}

public class OpenSpanTracker
{
    private readonly ConcurrentDictionary<string, OpenSpanInfo> _openSpans = new();

    public void TrackOpen(LogEvent evt)
    {
        if (evt.SpanId == null) return;
        _openSpans[evt.SpanId] = new OpenSpanInfo
        {
            SpanId = evt.SpanId,
            SignalId = evt.SignalId,
            Scope = evt.Scope,
            GroupName = evt.GroupName,
            Name = evt.Name,
            StartedAt = evt.Timestamp
        };
    }

    public void TrackClose(string spanId)
    {
        _openSpans.TryRemove(spanId, out _);
    }

    public IReadOnlyCollection<OpenSpanInfo> GetCurrentlyRunning() => _openSpans.Values.ToList();
}
```

- [ ] **Step 3: 创建 TokenAggregator**

```csharp
// AgentCoreProcessor/Logging/TokenAggregator.cs
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentCoreProcessor.Logging;

public class TokenAggregator
{
    private readonly LogDatabase _db;

    public TokenAggregator(LogDatabase db) => _db = db;

    public void ProcessBatch(List<LogEvent> batch)
    {
        foreach (var evt in batch)
        {
            if (evt.Type != "close" || evt.GroupName != LogGroup.Model || evt.Detail == null)
                continue;

            try
            {
                using var doc = JsonDocument.Parse(evt.Detail);
                var root = doc.RootElement;

                using var cmd = _db.Connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO token_usage (timestamp, model, caller_tag, tokens_in, tokens_out, cached_in, elapsed_ms, success)
                    VALUES (@ts, @model, @caller, @tin, @tout, @cached, @elapsed, @success)
                    """;
                cmd.Parameters.AddWithValue("@ts", evt.Timestamp);
                cmd.Parameters.AddWithValue("@model", root.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "");
                cmd.Parameters.AddWithValue("@caller", root.TryGetProperty("caller_tag", out var c) ? c.GetString() ?? "" : "");
                cmd.Parameters.AddWithValue("@tin", root.TryGetProperty("tokens_in", out var ti) ? ti.GetInt32() : 0);
                cmd.Parameters.AddWithValue("@tout", root.TryGetProperty("tokens_out", out var to) ? to.GetInt32() : 0);
                cmd.Parameters.AddWithValue("@cached", root.TryGetProperty("cached_in", out var ci) ? ci.GetInt32() : 0);
                cmd.Parameters.AddWithValue("@elapsed", root.TryGetProperty("elapsed_ms", out var el) ? el.GetInt32() : 0);
                cmd.Parameters.AddWithValue("@success", root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null ? 0 : 1);
                cmd.ExecuteNonQuery();
            }
            catch { }
        }
    }
}
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build AgentCoreProcessor`

- [ ] **Step 5: Commit**

```bash
git add AgentCoreProcessor/Logging/LogWriter.cs AgentCoreProcessor/Logging/OpenSpanTracker.cs AgentCoreProcessor/Logging/TokenAggregator.cs
git commit -m "feat(logging): add LogWriter with batch flush, OpenSpanTracker, TokenAggregator"
```

---

### Task 3: SignalContext 与静态门面 API

**Files:**
- Create: `AgentCoreProcessor/Logging/SignalContext.cs`
- Create: `AgentCoreProcessor/Logging/Signal.cs`

- [ ] **Step 1: 创建 SignalContext**

```csharp
// AgentCoreProcessor/Logging/SignalContext.cs
using System.Text.Json;

namespace AgentCoreProcessor.Logging;

public class SignalContext
{
    private static readonly AsyncLocal<SignalContext?> _current = new();
    public static SignalContext? Current => _current.Value;

    public string SignalId { get; init; } = "";
    public string Scope { get; init; } = "";
    public long Branch { get; private set; }
    public long? CurrentSpanId { get; private set; }

    private static LogWriter? _writer;
    private static int _minLevel = LogLevel.Info;

    public static void Init(LogWriter writer, int minLevel = LogLevel.Info)
    {
        _writer = writer;
        _minLevel = minLevel;
    }

    public static void SetMinLevel(int level) => _minLevel = level;

    public static SignalContext Begin(string group, string scope, string name, object? detail = null)
    {
        var ctx = new SignalContext
        {
            SignalId = $"sig-{Guid.NewGuid():N}",
            Scope = scope,
        };
        _current.Value = ctx;
        var spanId = GenerateSpanId();
        var evt = ctx.MakeEvent(group, "open", name, spanId, null, detail);
        _writer?.Enqueue(evt);
        ctx.Branch = evt.Id > 0 ? evt.Id : evt.Timestamp;
        ctx.CurrentSpanId = evt.Timestamp; // 用 timestamp 作为临时 ID，DB 写入后有真实 ID
        return ctx;
    }

    public static SignalContext Continue(string signalId, long? parentSpanId, string scope, string group, string name, object? detail = null)
    {
        var ctx = new SignalContext
        {
            SignalId = signalId,
            Scope = scope,
            CurrentSpanId = parentSpanId,
        };
        _current.Value = ctx;
        var spanId = GenerateSpanId();
        var evt = ctx.MakeEvent(group, "open", name, spanId, parentSpanId, detail);
        _writer?.Enqueue(evt);
        ctx.Branch = evt.Timestamp;
        ctx.CurrentSpanId = evt.Timestamp;
        return ctx;
    }

    public static void Restore(SignalContext? ctx) => _current.Value = ctx;

    public SpanHandle Open(string group, string name, object? detail = null)
    {
        var spanId = GenerateSpanId();
        var parentId = CurrentSpanId;
        var evt = MakeEvent(group, "open", name, spanId, parentId, detail);
        _writer?.Enqueue(evt);
        var prevSpanId = CurrentSpanId;
        CurrentSpanId = evt.Timestamp;
        return new SpanHandle(this, group, spanId, prevSpanId);
    }

    public void Close(string group, string spanId, object? detail = null)
    {
        var evt = MakeEvent(group, "close", "", spanId, CurrentSpanId, detail);
        _writer?.EnqueueClose(evt);
    }

    public void Event(string group, string name, int level = LogLevel.Info, object? detail = null)
    {
        if (level < _minLevel) return;
        var evt = MakeEvent(group, "event", name, null, CurrentSpanId, detail);
        evt.Level = level;
        _writer?.Enqueue(evt);
    }

    private LogEvent MakeEvent(string group, string type, string name, string? spanId, long? parentId, object? detail)
    {
        return new LogEvent
        {
            SignalId = SignalId,
            Scope = Scope,
            Branch = Branch,
            ParentId = parentId,
            SpanId = spanId,
            GroupName = group,
            Level = LogLevel.Info,
            Type = type,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Name = name,
            Detail = detail != null ? JsonSerializer.Serialize(detail) : null
        };
    }

    private static string GenerateSpanId() => Guid.NewGuid().ToString("N")[..16];
}

public class SpanHandle : IDisposable
{
    private readonly SignalContext _ctx;
    private readonly string _group;
    private readonly string _spanId;
    private readonly long? _prevSpanId;
    private object? _closeDetail;
    private bool _disposed;

    internal SpanHandle(SignalContext ctx, string group, string spanId, long? prevSpanId)
    {
        _ctx = ctx;
        _group = group;
        _spanId = spanId;
        _prevSpanId = prevSpanId;
    }

    public void SetCloseDetail(object detail) => _closeDetail = detail;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ctx.Close(_group, _spanId, _closeDetail);
        _ctx.CurrentSpanId = _prevSpanId;
    }
}
```

- [ ] **Step 2: 创建 Signal 静态门面**

```csharp
// AgentCoreProcessor/Logging/Signal.cs
namespace AgentCoreProcessor.Logging;

public static class Signal
{
    public static SignalContext Begin(string group, string scope, string name, object? detail = null)
        => SignalContext.Begin(group, scope, name, detail);

    public static SignalContext Continue(string signalId, long? parentSpanId, string scope, string group, string name, object? detail = null)
        => SignalContext.Continue(signalId, parentSpanId, scope, group, name, detail);

    public static SpanHandle Open(string group, string name, object? detail = null)
    {
        var ctx = SignalContext.Current;
        if (ctx == null) return SpanHandle.Noop;
        return ctx.Open(group, name, detail);
    }

    public static void Event(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Info, detail);

    public static void Debug(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Debug, detail);

    public static void Warn(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Warn, detail);

    public static void Error(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Error, detail);
}
```

注意：`SpanHandle.Noop` 需要在 SpanHandle 类中添加一个静态空实现，Dispose 时什么都不做。

- [ ] **Step 3: 编译验证**

Run: `dotnet build AgentCoreProcessor`

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/Logging/SignalContext.cs AgentCoreProcessor/Logging/Signal.cs
git commit -m "feat(logging): add SignalContext with AsyncLocal propagation and Signal facade API"
```

---

### Task 4: SDK 接口层（插件/组件可用）

**Files:**
- Create: `AgentLilara.PluginSDK/Logging/ISignalLogger.cs`
- Create: `AgentLilara.PluginSDK/Logging/ILogAccess.cs`
- Create: `AgentLilara.PluginSDK/Logging/LogEventInfo.cs`

- [ ] **Step 1: 创建 ISignalLogger（通用写入接口）**

```csharp
// AgentLilara.PluginSDK/Logging/ISignalLogger.cs
namespace AgentLilara.PluginSDK.Logging;

public interface ISignalLogger
{
    IDisposable Open(string group, string name, object? detail = null);
    void Event(string group, string name, object? detail = null);
    void Debug(string group, string name, object? detail = null);
    void Warn(string group, string name, object? detail = null);
    void Error(string group, string name, object? detail = null);
}
```

- [ ] **Step 2: 创建数据传输类型**

```csharp
// AgentLilara.PluginSDK/Logging/LogEventInfo.cs
namespace AgentLilara.PluginSDK.Logging;

public class LogEventInfo
{
    public long Id { get; set; }
    public string SignalId { get; set; } = "";
    public string Scope { get; set; } = "";
    public long Branch { get; set; }
    public long? ParentId { get; set; }
    public string? SpanId { get; set; }
    public string GroupName { get; set; } = "";
    public int Level { get; set; }
    public string Type { get; set; } = "";
    public long Timestamp { get; set; }
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
}

public class OpenSpanSummary
{
    public string SpanId { get; set; } = "";
    public string SignalId { get; set; } = "";
    public string Scope { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string Name { get; set; } = "";
    public long StartedAt { get; set; }
}

public class TokenUsageInfo
{
    public long Timestamp { get; set; }
    public string Model { get; set; } = "";
    public string? CallerTag { get; set; }
    public int TokensIn { get; set; }
    public int TokensOut { get; set; }
    public int CachedIn { get; set; }
    public int ElapsedMs { get; set; }
    public bool Success { get; set; }
}
```

- [ ] **Step 3: 创建 ILogAccess（高级读写接口）**

```csharp
// AgentLilara.PluginSDK/Logging/ILogAccess.cs
namespace AgentLilara.PluginSDK.Logging;

public interface ILogAccess : ISignalLogger
{
    List<LogEventInfo> GetBySignal(string signalId);
    List<LogEventInfo> GetByScope(string scope, long? since = null, int limit = 200);
    List<LogEventInfo> GetRecent(int limit = 200, string? group = null, int? minLevel = null);
    List<OpenSpanSummary> GetOpenSpans();
    List<LogEventInfo> GetSignalList(int limit = 50);
    List<TokenUsageInfo> GetTokenUsage(long? since = null, string? model = null, string? callerTag = null);
    IDisposable Subscribe(Action<IReadOnlyList<LogEventInfo>> callback);
    void Cleanup(int? retainDays = null);
}
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build AgentLilara.PluginSDK`

- [ ] **Step 5: Commit**

```bash
git add AgentLilara.PluginSDK/Logging/
git commit -m "feat(sdk): add ISignalLogger and ILogAccess interfaces for plugin logging"
```

---

### Task 5: 查询接口（主程序实现）

**Files:**
- Create: `AgentCoreProcessor/Logging/ILogQuery.cs`
- Create: `AgentCoreProcessor/Logging/LogQuery.cs`
- Create: `AgentCoreProcessor/Logging/LogAccessImpl.cs`

- [ ] **Step 1: 创建查询接口和实现**

（保持原有 ILogQuery + LogQuery 代码不变）

- [ ] **Step 2: 创建 LogAccessImpl（SDK 接口的宿主实现）**

```csharp
// AgentCoreProcessor/Logging/LogAccessImpl.cs
using AgentLilara.PluginSDK.Logging;

namespace AgentCoreProcessor.Logging;

public class LogAccessImpl : ILogAccess
{
    private readonly LogWriter _writer;
    private readonly LogQuery _query;
    private readonly OpenSpanTracker _spanTracker;
    private readonly LogDatabase _db;

    public LogAccessImpl(LogWriter writer, LogQuery query, OpenSpanTracker spanTracker, LogDatabase db)
    {
        _writer = writer;
        _query = query;
        _spanTracker = spanTracker;
        _db = db;
    }

    // ISignalLogger 实现 — 委托给 SignalContext.Current
    public IDisposable Open(string group, string name, object? detail = null)
        => SignalContext.Current?.Open(group, name, detail) ?? SpanHandle.Noop;

    public void Event(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Info, detail);

    public void Debug(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Debug, detail);

    public void Warn(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Warn, detail);

    public void Error(string group, string name, object? detail = null)
        => SignalContext.Current?.Event(group, name, LogLevel.Error, detail);

    // ILogAccess 查询实现 — 委托给 LogQuery，转换类型
    public List<LogEventInfo> GetBySignal(string signalId)
        => _query.GetBySignal(signalId).Select(ToInfo).ToList();

    public List<LogEventInfo> GetByScope(string scope, long? since = null, int limit = 200)
        => _query.GetByScope(scope, since, limit).Select(ToInfo).ToList();

    public List<LogEventInfo> GetRecent(int limit = 200, string? group = null, int? minLevel = null)
        => _query.GetRecent(limit, group, minLevel).Select(ToInfo).ToList();

    public List<OpenSpanSummary> GetOpenSpans()
        => _spanTracker.GetCurrentlyRunning().Select(s => new OpenSpanSummary
        {
            SpanId = s.SpanId, SignalId = s.SignalId, Scope = s.Scope,
            GroupName = s.GroupName, Name = s.Name, StartedAt = s.StartedAt
        }).ToList();

    public List<LogEventInfo> GetSignalList(int limit = 50)
        => _query.GetSignalList(limit).Select(ToInfo).ToList();

    public List<TokenUsageInfo> GetTokenUsage(long? since = null, string? model = null, string? callerTag = null)
        => _query.GetTokenUsage(since, model, callerTag).Select(t => new TokenUsageInfo
        {
            Timestamp = t.Timestamp, Model = t.Model, CallerTag = t.CallerTag,
            TokensIn = t.TokensIn, TokensOut = t.TokensOut, CachedIn = t.CachedIn,
            ElapsedMs = t.ElapsedMs, Success = t.Success
        }).ToList();

    public IDisposable Subscribe(Action<IReadOnlyList<LogEventInfo>> callback)
        => _writer.Subscribe(batch => callback(batch.Select(ToInfo).ToList()));

    public void Cleanup(int? retainDays = null)
        => _db.Cleanup(retainDays ?? 7, 90);

    private static LogEventInfo ToInfo(LogEvent e) => new()
    {
        Id = e.Id, SignalId = e.SignalId, Scope = e.Scope, Branch = e.Branch,
        ParentId = e.ParentId, SpanId = e.SpanId, GroupName = e.GroupName,
        Level = e.Level, Type = e.Type, Timestamp = e.Timestamp,
        Name = e.Name, Detail = e.Detail
    };
}
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build AgentCoreProcessor`

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/Logging/ILogQuery.cs AgentCoreProcessor/Logging/LogQuery.cs AgentCoreProcessor/Logging/LogAccessImpl.cs
git commit -m "feat(logging): add LogQuery, LogAccessImpl bridging SDK interfaces"
```

---

### Task 6: 系统集成 — 初始化与注册

**Files:**
- Modify: `AgentCoreProcessor/Program.cs`
- Modify: `AgentCoreProcessor/Engine/Core/MasterEngine.cs`

- [ ] **Step 1: 在 Program.cs 中初始化日志系统**

在服务注册阶段添加：

```csharp
// 日志系统初始化
var logDb = new LogDatabase(Path.Combine(AppContext.BaseDirectory, "Storage"));
var spanTracker = new OpenSpanTracker();
var tokenAggregator = new TokenAggregator(logDb);
var logWriter = new LogWriter(logDb, spanTracker, tokenAggregator);
var logQuery = new LogQuery(logDb);
var logAccess = new LogAccessImpl(logWriter, logQuery, spanTracker, logDb);

SignalContext.Init(logWriter);

builder.Services.AddSingleton(logDb);
builder.Services.AddSingleton(logWriter);
builder.Services.AddSingleton(spanTracker);
builder.Services.AddSingleton<ILogQuery>(logQuery);
builder.Services.AddSingleton<ILogAccess>(logAccess);
builder.Services.AddSingleton<ISignalLogger>(logAccess);
```

- [ ] **Step 2: 在 PluginLoader 中注入 ISignalLogger / ILogAccess**

类似 `IMemoryAccess` 的注入方式，扫描插件的 `[PluginService]` 属性，注入对应接口实例。

- [ ] **Step 3: 在 MasterEngine.InitAsync 中移除 ModelCallLogRepository**

移除旧代码，添加启动时清理：

```csharp
logDb.Cleanup(retainDays: 7, tokenRetainDays: 90);
```

- [ ] **Step 4: 在睡眠进入时触发清理**

- [ ] **Step 5: 编译验证**

- [ ] **Step 6: Commit**

```bash
git add AgentCoreProcessor/Program.cs AgentCoreProcessor/Engine/Core/MasterEngine.cs AgentCoreProcessor/Tool/Host/PluginLoader.cs
git commit -m "feat(logging): integrate log system init, DI registration, plugin injection"
```

---

### Task 7: 埋点 — 适配器层

**Files:**
- Modify: `AgentCoreProcessor/Adapter/AdapterManager.cs`（或具体 Adapter 实现）

- [ ] **Step 1: 在消息接收处生成信号**

在 `OnMessageReceived` 事件处理中，创建信号入口：

```csharp
// AdapterManager 中消息接收回调
private void OnAdapterMessage(IncomingMessage msg)
{
    using var sigCtx = Signal.Begin(LogGroup.Adapter, $"adapter:{msg.Platform}", "消息接收", new
    {
        platform = msg.Platform,
        sender = msg.SenderId,
        channel = msg.ChannelId,
        msg_type = msg.Type.ToString()
    });

    // 原有逻辑：发布到 EventBus
    _eventBus.PublishMessage(msg);
}
```

- [ ] **Step 2: 在消息发送处记录**

在适配器发送消息时：

```csharp
using (var span = Signal.Open(LogGroup.Adapter, "消息发送", new { target = channelId, type = msgType }))
{
    var result = await adapter.SendAsync(msg);
    span.SetCloseDetail(new { success = result.Success, elapsed_ms = sw.ElapsedMilliseconds, error = result.Error });
}
```

- [ ] **Step 3: 编译验证**

- [ ] **Step 4: Commit**

```bash
git commit -am "feat(logging): instrument adapter layer with signal generation"
```

---

### Task 8: 埋点 — EventBus 信号传递

**Files:**
- Modify: `AgentCoreProcessor/Engine/Core/EventBus.cs`

- [ ] **Step 1: 在消息分发时传递信号上下文**

EventBus 发布消息时，将当前 SignalContext 的 signal_id 和 current span_id 附加到事件中（通过 MessageEvent 的扩展字段或 AsyncLocal 自然传播）。

```csharp
// EventBus.PublishMessage
public void PublishMessage(IncomingMessage msg)
{
    Signal.Event(LogGroup.Engine, "总线分发", detail: new { channel = msg.ChannelId });
    // 原有分发逻辑...
    // AsyncLocal 会自动传播到订阅者的回调中
}
```

- [ ] **Step 2: 编译验证**

- [ ] **Step 3: Commit**

```bash
git commit -am "feat(logging): instrument EventBus message dispatch"
```

---

### Task 9: 埋点 — 频道循环

**Files:**
- Modify: `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs`

- [ ] **Step 1: 在 RunAsync 主循环中埋点**

频道循环是信号的主要消费者。在 `RunAsync` 的关键步骤中：

```csharp
// ChannelEngine.RunAsync 中
// 收到消息/被唤醒时，继承或创建信号
var sigCtx = SignalContext.Current != null
    ? SignalContext.Current  // AsyncLocal 已传播
    : Signal.Begin(LogGroup.Engine, $"channel:{_channelId}", "频道唤醒", new { trigger = "impulse" });

// 闸门评估
using (Signal.Open(LogGroup.Engine, "闸门评估", new { impulse, threshold }))
{
    var gateResult = EvaluateGate();
    // close detail 由 SetCloseDetail 设置
}

// 上下文组装
using (Signal.Open(LogGroup.Memory, "上下文组装", new { strategy = "semantic+recent" }))
{
    await PrepareContextAsync();
}

// 模型调用
using (var span = Signal.Open(LogGroup.Model, "模型调用", new { model = coreName, messages_count = messages.Count }))
{
    var output = await agentCore.InvokeAsync(messages);
    span.SetCloseDetail(new { tokens_out = output.Usage?.OutputTokens, tool_calls = output.ToolCalls?.Count ?? 0 });
}

// 工具执行（如有）
foreach (var toolCall in output.ToolCalls)
{
    using (var span = Signal.Open(LogGroup.Tool, "工具执行", new { tool = toolCall.Name, args = toolCall.Arguments }))
    {
        var result = await ExecuteTool(toolCall);
        span.SetCloseDetail(new { success = result.Success, elapsed_ms = sw.ElapsedMilliseconds });
    }
}

// 挂起
Signal.Event(LogGroup.Engine, "频道挂起");
```

- [ ] **Step 2: 信号吸收处理**

当频道正在处理中收到新消息时：

```csharp
// 在 CollectBuffer 或消息入队处
if (SignalContext.Current != null)
{
    Signal.Event(LogGroup.Engine, "吸收信号", detail: new
    {
        absorbed_signal = incomingSignalId,
        from = msg.SenderId,
        summary = msg.Content?.Substring(0, Math.Min(50, msg.Content.Length))
    });
}
```

- [ ] **Step 3: 编译验证**

- [ ] **Step 4: Commit**

```bash
git commit -am "feat(logging): instrument ChannelEngine with signal tracking"
```

---

### Task 10: 埋点 — 模型调用层（CoreBase）

**Files:**
- Modify: `AgentCoreProcessor/Core/CoreBase.cs`

- [ ] **Step 1: 替换 LogOutput 为 Signal API**

在 `GenerateWithToolsAsync` / `GenerateAsync` 中，移除旧的 `LogOutput()` 调用和 `CallLogRepo` 写入，改为：

```csharp
// CoreBase 中模型调用
// 流式首 token 回调中
Signal.Debug(LogGroup.Model, "首token到达", new { elapsed_ms = sw.ElapsedMilliseconds });

// 流式进度（每 500 tokens）
Signal.Debug(LogGroup.Model, "流式进行中", new { tokens = currentTokenCount });

// 调用完成后（原 LogOutput 位置）
// 不再写 JSON 文件和 ModelCallLog 表
// close detail 由调用方（ChannelEngine）的 span.SetCloseDetail 处理
// 但 token 信息需要在这里通过 event 或 close detail 传出

// 如果 CoreBase 自己管理 span（而非调用方）：
// span.SetCloseDetail(new { model, tokens_in, tokens_out, cached_in, elapsed_ms, caller_tag, error });
```

- [ ] **Step 2: 移除 LogOutput 方法和 CallLogRepo 静态字段**

```csharp
// 删除:
// public static ModelCallLogRepository? CallLogRepo { get; set; }
// private void LogOutput(...) { ... }
```

- [ ] **Step 3: 移除模型 JSON 日志文件写入**

不再写 `Storage/Logs/Model/*.json` 文件。所有信息通过 Signal 的 detail 字段记录。

- [ ] **Step 4: 编译验证**

- [ ] **Step 5: Commit**

```bash
git commit -am "feat(logging): replace CoreBase.LogOutput with Signal API, remove ModelCallLog writes"
```

---

### Task 11: 埋点 — 系统循环与委托

**Files:**
- Modify: `AgentCoreProcessor/Engine/System/SystemEngine.cs`
- Modify: `AgentCoreProcessor/Engine/Core/DelegationRegistry.cs`（或相关委托处理）

- [ ] **Step 1: SystemEngine 埋点**

```csharp
// SystemEngine 主循环
using (Signal.Begin(LogGroup.Engine, "system", "系统循环轮次"))
{
    // 任务检查
    Signal.Event(LogGroup.Engine, "任务队列检查", new { pending = taskCount });

    // 委托处理
    using (Signal.Open(LogGroup.Engine, "委托评估", new { delegation_id = d.Id }))
    {
        var decision = Evaluate(d);
        // close detail: { result: "accept"/"reject"/"queue" }
    }
}
```

- [ ] **Step 2: 委托唤醒时传递信号**

```csharp
// 委托唤醒目标频道时，传递 SignalHandoff
var handoff = new SignalHandoff
{
    SignalId = SignalContext.Current?.SignalId ?? Signal.NewId(),
    ParentSpanId = SignalContext.Current?.CurrentSpanId
};
targetChannel.Wake(handoff);
```

- [ ] **Step 3: 编译验证**

- [ ] **Step 4: Commit**

```bash
git commit -am "feat(logging): instrument SystemEngine and delegation with signal propagation"
```

---

### Task 12: 移除旧日志系统

**Files:**
- Delete: `AgentCoreProcessor/Engine/Core/FrameworkLogger.cs`
- Delete: `AgentCoreProcessor/Database/ModelCallLog.cs`
- Delete: `AgentCoreProcessor/Database/ModelCallLogRepository.cs`
- Delete: `AgentCoreProcessor/WebUI/Services/LogStreamService.cs`
- Delete: `AgentCoreProcessor/WebUI/Components/Pages/Logs.razor`
- Delete: `AgentCoreProcessor/WebUI/Components/Pages/Logs_Model.razor`
- Delete: `AgentCoreProcessor/WebUI/Components/Pages/Logs_Tokens.razor`
- Modify: 所有引用 FrameworkLogger 的文件

- [ ] **Step 1: 查找所有 FrameworkLogger 引用**

Run: `grep -r "FrameworkLogger" --include="*.cs" -l`

将所有 `FrameworkLogger.Log(...)` 调用替换为对应的 `Signal.Event(...)` 或 `Signal.Warn(...)` / `Signal.Error(...)`。

替换映射：
- `FrameworkLogger.Log(source, msg)` → `Signal.Event(LogGroup.Engine, msg)`
- `FrameworkLogger.LogError(source, ex, ctx)` → `Signal.Error(group, ctx, new { exception = ex.Message, stack = ex.StackTrace })`
- `FrameworkLogger.LogToolCall(source, name, id, status)` → 已被 Task 8 的工具 span 覆盖
- `FrameworkLogger.LogModelCall(...)` → 已被 Task 9 覆盖
- `FrameworkLogger.LogMemoryRecall(...)` → `Signal.Event(LogGroup.Memory, "记忆检索完成", new { count, tempCount })`
- `FrameworkLogger.LogClassification(...)` → `Signal.Event(LogGroup.Engine, "消息分类", new { category })`
- `FrameworkLogger.LogPermission(...)` → `Signal.Event(LogGroup.Engine, "权限检查", new { userId, level, allowed })`

- [ ] **Step 2: 移除 LogStreamService 及其注册**

从 `Program.cs` 的 DI 注册中移除 `LogStreamService`。删除文件。

- [ ] **Step 3: 移除旧日志 WebUI 页面**

删除 `Logs.razor`、`Logs_Model.razor`、`Logs_Tokens.razor`。从导航菜单中移除对应链接。

- [ ] **Step 4: 删除旧文件**

```bash
rm AgentCoreProcessor/Engine/Core/FrameworkLogger.cs
rm AgentCoreProcessor/Database/ModelCallLog.cs
rm AgentCoreProcessor/Database/ModelCallLogRepository.cs
rm AgentCoreProcessor/WebUI/Services/LogStreamService.cs
rm AgentCoreProcessor/WebUI/Components/Pages/Logs.razor
rm AgentCoreProcessor/WebUI/Components/Pages/Logs_Model.razor
rm AgentCoreProcessor/WebUI/Components/Pages/Logs_Tokens.razor
```

- [ ] **Step 5: 编译验证**

Run: `dotnet build AgentCoreProcessor`
确保无编译错误（所有旧引用已替换）。

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(logging): remove FrameworkLogger, ModelCallLog, old log pages; replace all usages with Signal API"
```

---

### Task 13: 启动阶段埋点

**Files:**
- Modify: `AgentCoreProcessor/Program.cs`
- Modify: `AgentCoreProcessor/Engine/Core/MasterEngine.cs`

- [ ] **Step 1: 程序启动信号**

```csharp
// Program.cs 最早处
using var startupSignal = Signal.Begin(LogGroup.Engine, "system", "程序启动", new
{
    version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
    args = string.Join(" ", args)
});

// 各初始化步骤
Signal.Event(LogGroup.Engine, "配置加载完成");
Signal.Event(LogGroup.Engine, "服务注册完成");
```

- [ ] **Step 2: MasterEngine 子系统启动**

```csharp
// MasterEngine.InitAsync 中
Signal.Event(LogGroup.Engine, "数据库初始化完成");
Signal.Event(LogGroup.Plugin, "插件加载完成", new { count = pluginCount });
Signal.Event(LogGroup.Engine, "引擎就绪");
```

- [ ] **Step 3: 编译验证**

- [ ] **Step 4: Commit**

```bash
git commit -am "feat(logging): instrument startup sequence"
```

---

### Task 14: 关闭与异常边界

**Files:**
- Modify: `AgentCoreProcessor/Program.cs`（关闭钩子）
- Modify: 各引擎的异常处理

- [ ] **Step 1: 优雅关闭记录**

```csharp
// Program.cs 或 MasterEngine 关闭逻辑
lifetime.ApplicationStopping.Register(() =>
{
    using var shutdownSignal = Signal.Begin(LogGroup.Engine, "system", "程序关闭");
    // 各子系统关闭...
    Signal.Event(LogGroup.Engine, "所有引擎已停止");
    logWriter.Dispose(); // 确保最后一批刷盘
});
```

- [ ] **Step 2: 全局异常边界**

在各引擎的 try/catch 顶层：

```csharp
catch (Exception ex)
{
    Signal.Error(LogGroup.Engine, "未捕获异常", new
    {
        exception = ex.GetType().Name,
        message = ex.Message,
        stack = ex.StackTrace
    });
}
```

- [ ] **Step 3: 编译验证**

- [ ] **Step 4: Commit**

```bash
git commit -am "feat(logging): instrument shutdown sequence and exception boundaries"
```

---

### Task 15: 集成测试与验证

**Files:**
- 无新文件，运行验证

- [ ] **Step 1: 完整编译**

Run: `dotnet build AgentCoreProcessor`
确保零错误零警告（与日志相关的）。

- [ ] **Step 2: 启动验证**

Run: `dotnet run --project AgentCoreProcessor`
确认：
- `Storage/logs.db` 被创建
- 启动阶段的信号被写入（查询 events 表）
- 无运行时异常

- [ ] **Step 3: 发送测试消息验证完整链路**

通过控制台适配器或 WebUI 模拟对话发送消息，确认：
- 适配器层生成 signal_id
- 频道循环继承信号
- 模型调用的 open/close 被记录
- token_usage 表有对应记录
- OpenSpanTracker 在模型调用期间能查到 open span

- [ ] **Step 4: 验证清理逻辑**

手动调用 `logDb.Cleanup(0, 0)` 确认能清空表。

- [ ] **Step 5: Commit**

```bash
git commit -am "chore(logging): verify integration, all systems operational"
```

---

### Task 16: 文档更新

**Files:**
- Modify: `docs/architecture-map.md`
- Modify: `docs/architecture.md`（如有日志相关章节）
- Modify: `CLAUDE.md`（更新关键路径）

- [ ] **Step 1: 更新 architecture-map.md**

添加日志系统章节：
```markdown
## 日志系统

- 入口 API：`Logging/Signal.cs`（静态门面）
- 上下文传播：`Logging/SignalContext.cs`（AsyncLocal）
- 写入引擎：`Logging/LogWriter.cs`（Channel<T> + 批量刷盘）
- 数据库：`Storage/logs.db`（独立 SQLite）
- 查询：`Logging/LogQuery.cs`
- Token 聚合：`Logging/TokenAggregator.cs`
```

- [ ] **Step 2: 更新 CLAUDE.md 关键路径**

替换旧的日志相关条目：
```markdown
- Token 统计：Logging/TokenAggregator.cs（从 Model close 事件派生）
- 日志系统：Logging/Signal.cs（门面）+ Logging/LogWriter.cs（写入）+ Storage/logs.db
```

移除：
```markdown
- Token 统计：Database/ModelCallLog.cs + ModelCallLogRepository.cs
- 模型日志结构化：JSON 格式...
```

- [ ] **Step 3: Commit**

```bash
git commit -am "docs: update architecture docs for new logging system"
```

