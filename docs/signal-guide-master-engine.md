# Signal Guide: MasterEngine

> **Status:** ~15% complete. Scattered point events only — no lifecycle span, no phase spans.
> **Goal:** Add lifecycle span wrapping init/start/stop, phase spans per initialization step, error events.
> **Reference:** `signal-instrumentation-guide.md` for API conventions.

**File:** `AgentCoreProcessor/Engine/Core/MasterEngine.cs`

**Important:** All Signal names in the code must be in Chinese. Detail fields use English keys.
**Detail standard:** Full content in all details — no truncation. Full config paths, full error messages with stack traces, full counts and state. Only exclude binary data and secrets.

---

## Current State

Already present:
- `Signal.Event(LogGroup.Engine, "数据库初始化完成")` — L215
- `Signal.Event(LogGroup.Plugin, "插件加载完成", ...)` — L398
- `Signal.Event(LogGroup.Engine, "引擎就绪")` — L455
- `Signal.Error(LogGroup.Engine, "未捕获异常", ...)` — L581 (in StartEngine wrapper)

Missing:
- **Lifecycle span** — no `Signal.Begin` or `Signal.Continue` wrapping `InitAsync` or the overall MasterEngine lifetime
- **Phase spans** — each init phase (DB, embedding, vision, OCR, services, plugins, components, engines) should be a span
- **Error events** — many empty catch blocks in InitAsync
- **Shutdown event** — no event or span close for shutdown sequence

---

## Design Note

MasterEngine does NOT implement ISubEngine — it is the host/orchestrator. So it doesn't follow the engine lifecycle pattern exactly. Instead:

- `InitAsync()` is called once at startup (see Program.cs)
- `StartEngine()` launches sub-engines
- `RequestStopAll()` + `WaitAllStoppedAsync()` handles shutdown

The startup signal already exists in Program.cs. MasterEngine should **continue** from that signal using `Signal.Continue`, not `Signal.Begin`.

---

## Changes Needed

### 1. Add lifecycle span wrapping InitAsync

At the top of `InitAsync()` (after L209), add a `Signal.Continue` from the startup signal and wrap the entire method in a phase span:

```csharp
public async Task InitAsync()
{
    // Continue from the startup signal (set in Program.cs)
    var parentCtx = SignalContext.Current;
    var initCtx = Signal.Continue(
        parentCtx?.SignalId ?? Signal.NewId(),
        parentCtx?.CurrentSpanId,
        "system:init",
        LogGroup.Engine,
        "内核初始化",
        new { databasePath = databaseDirectory });

    try
    {
        Directory.CreateDirectory(databaseDirectory);
        // ... rest of InitAsync ...

        initCtx.Close(new { reason = "completed" });
    }
    catch (Exception ex)
    {
        Signal.Error(LogGroup.Engine, "内核初始化失败", new
        {
            error = ex.GetType().Name,
            message = ex.Message,
            stack = ex.StackTrace
        });
        initCtx.Close(new { reason = "failed", error = ex.Message });
    }
}
```

### 2. Add phase spans for each init step

Wrap each major initialization phase in its own span:

```csharp
// Phase 1: Database
using (var dbSpan = Signal.Open(LogGroup.Engine, "数据库初始化",
    new { path = dbPath, databaseDirectory }))
{
    db = new DbManager(dbPath);
    await db.InitAsync();
    // ... schema migrations ...
    dbSpan.SetCloseDetail(new
    {
        tablesCreated = true,
        dbPath,
        tableList = db.GetTableNames()     // list all tables after migration
    });
}
// Remove the old Signal.Event("数据库初始化完成") — the span close replaces it

// Phase 2: Repositories
using (var repoSpan = Signal.Open(LogGroup.Engine, "仓储初始化", new { }))
{
    // ... create all repositories ...
    repoSpan.SetCloseDetail(new
    {
        repositoryCount = 12,
        repositoryTypes = new[] { "User", "Person", "Channel", "Message", "TempMemory", /* ... */ }
    });
}

// Phase 3: Embedding provider
using (var embSpan = Signal.Open(LogGroup.Engine, "Embedding初始化",
    new { configPath = embConfigPath }))
{
    // ... load embedding config and create provider ...
    embSpan.SetCloseDetail(new
    {
        provider = embeddingProvider?.GetType().Name ?? "none",
        model = embModel,
        endpoint = embEndpoint,
        dimensions = embDimensions
    });
}

// Phase 4: Vision + OCR
using (var visSpan = Signal.Open(LogGroup.Engine, "视觉服务初始化",
    new
    {
        visionConfigPath = visionConfigPath,
        ocrConfigPath = ocrConfigPath
    }))
{
    // ... Vision and OCR setup ...
    visSpan.SetCloseDetail(new
    {
        visionAvailable = visionProvider != null,
        visionProviderType = visionProvider?.GetType().Name,
        visionModel = visionModel,
        ocrAvailable = ocrProvider != null,
        ocrProviderType = ocrProvider?.GetType().Name,
        ocrModel = ocrModel
    });
}

// Phase 5: Services
using (var svcSpan = Signal.Open(LogGroup.Engine, "核心服务初始化", new { }))
{
    MemorySvc = new MemoryService(...);
    Session = new SessionManager(...);
    // ... ImpulseConfig, TrustConfig ...
    svcSpan.SetCloseDetail(new
    {
        memoryServiceReady = MemorySvc != null,
        sessionManagerReady = Session != null,
        impulseConfig = new { threshold = ctx.ImpulseConfig.Threshold, bufferWindow = ctx.ImpulseConfig.BufferWindowSeconds },
        trustConfig = new { dailyCap = ctx.TrustConfig.DailyInteractionCap, increment = ctx.TrustConfig.DailyInteractionIncrement }
    });
}

// Phase 6: Persona seed
using (var seedSpan = Signal.Open(LogGroup.Memory, "人设记忆种子加载",
    new { seedFilePath = personaSeedPath }))
{
    await LoadPersonaMemorySeedAsync();
    seedSpan.SetCloseDetail(new
    {
        linesLoaded = loaded,
        personasSeeded = personaCount
    });
}

// Phase 7: TaskBridge + DelegationRegistry
using (var bridgeSpan = Signal.Open(LogGroup.Engine, "通信桥梁初始化",
    new { systemLoopPath }))
{
    TaskBridge = new TaskBridge(systemLoopPath);
    Delegations = new DelegationRegistry(systemLoopPath);
    // ... wire up callbacks ...
    bridgeSpan.SetCloseDetail(new
    {
        taskBridgeReady = TaskBridge != null,
        delegationRegistryReady = Delegations != null,
        systemLoopPath
    });
}

// Phase 8: Plugin loading
using (var pluginSpan = Signal.Open(LogGroup.Plugin, "插件加载",
    new { pluginDirectory = pluginDir }))
{
    // ... register core tools, load plugins ...
    pluginSpan.SetCloseDetail(new
    {
        coreTools = coreToolNames,
        pluginCount = pluginLoader.LoadedCount,
        pluginNames = pluginLoader.LoadedPlugins.Select(p => p.Name).ToList(),
        totalTools = Tool.ToolRegistry.All.Count,
        toolNames = Tool.ToolRegistry.All.Select(t => t.Name).ToList()
    });
}
// Remove the old Signal.Event("插件加载完成") — the span close replaces it

// Phase 9: Component system
using (var compSpan = Signal.Open(LogGroup.Plugin, "Component系统初始化",
    new { }))
{
    globalComponentHost = new GlobalComponentHost(...);
    await globalComponentHost.InitAsync();
    compSpan.SetCloseDetail(new
    {
        globalComponents = globalComponentHost?.GetRegisteredComponents().Select(c => c.GetType().Name).ToList(),
        componentCount = globalComponentHost?.ComponentCount ?? 0
    });
}
```

### 3. Enhance existing events into spans or richer events

**"引擎就绪"** — replace the point event with a span close or enhance it:
```csharp
// Replace L455 Signal.Event with:
initCtx.SetCloseDetail(new
{
    autoStartEngines = engineCfg.AutoStart.Count,
    spawnedEngines = spawnCount,
    reason = "completed"
});
```

### 4. Add StartEngine span

In `StartEngine()` (L570), add a span for each engine start:
```csharp
public ISubEngine StartEngine(ISubEngine engine)
{
    Signal.Event(LogGroup.Engine, "启动引擎", new
    {
        engineType = engine.EngineType,
        isInfrastructure = engine.IsInfrastructure,
        activeEngineCount = activeEngines.Count + 1
    });

    lock (engineLock) { activeEngines.Add(engine); }
    // ... rest of method ...
}
```

### 5. Add shutdown event

If there's a shutdown method (or in Program.cs shutdown sequence):
```csharp
Signal.Event(LogGroup.Engine, "引擎关闭", new
{
    activeEngineCount = activeEngines.Count(e => e.IsAlive),
    engineTypes = activeEngines.Where(e => e.IsAlive).Select(e => e.EngineType).ToList()
});
```

### 6. Add error events in empty catch blocks

MasterEngine has several empty catch blocks that swallow errors silently. Add `Signal.Error` or `Signal.Warn` to each:

- Embedding config loading failures:
  ```csharp
  Signal.Warn(LogGroup.Engine, "Embedding配置加载失败",
      new { file = Path.GetFileName(file), error = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace });
  ```
- Vision provider init failure (L321):
  ```csharp
  Signal.Warn(LogGroup.Engine, "Vision服务初始化失败",
      new { error = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace, configPath = visionConfigPath });
  ```
- OCR provider init failure (L349):
  ```csharp
  Signal.Warn(LogGroup.Engine, "OCR服务初始化失败",
      new { error = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace, configPath = ocrConfigPath });
  ```
- MCP server init failure (L452):
  ```csharp
  Signal.Error(LogGroup.Plugin, "MCP服务初始化失败",
      new { error = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace });
  ```
- Personality seed loading failure (L659):
  ```csharp
  Signal.Warn(LogGroup.Memory, "人设记忆种子加载失败",
      new { error = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace, seedPath = personaSeedPath });
  ```
- Scheduled task check failure (L532):
  ```csharp
  Signal.Error(LogGroup.Engine, "定时任务检查失败",
      new { error = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace });
  ```
- HandleEventCore SpawnCheck exceptions (L500):
  ```csharp
  Signal.Error(LogGroup.Engine, "SpawnCheck异常",
      new { error = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace, engineType = engine?.EngineType });
  ```
- HandleEventCore dispatch exceptions (L513):
  ```csharp
  Signal.Error(LogGroup.Engine, "引擎事件分发异常",
      new { error = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace, eventType = e?.GetType().Name });
  ```

### 7. Note about StartEngine error event

The existing `Signal.Error` at L581 is already good. Keep it. Enhance detail:
```csharp
Signal.Error(LogGroup.Engine, "引擎崩溃", new
{
    engineType = engine.EngineType,
    engineInstanceId = engine.GetHashCode(),
    exception = ex.GetType().Name,
    message = ex.Message,
    stack = ex.StackTrace                       // FULL stack, no truncation
});
```

---

## Naming Conventions

| Phase | Name | Group |
|-------|------|-------|
| Overall init | `内核初始化` | Engine |
| Database | `数据库初始化` | Engine |
| Repositories | `仓储初始化` | Engine |
| Embedding | `Embedding初始化` | Engine |
| Vision/OCR | `视觉服务初始化` | Engine |
| Services | `核心服务初始化` | Engine |
| Persona seed | `人设记忆种子加载` | Memory |
| Bridge | `通信桥梁初始化` | Engine |
| Plugin loading | `插件加载` | Plugin |
| Components | `Component系统初始化` | Plugin |
| Engine start | `启动引擎` (event) | Engine |
| Engine crash | `引擎崩溃` (error) | Engine |
| Shutdown | `引擎关闭` (event) | Engine |

---

## Checklist

- [ ] Lifecycle `Signal.Continue` wraps entire `InitAsync` from startup signal
- [ ] Each init phase has a named span with close detail
- [ ] Point events (`Signal.Event`) replaced by span close details where possible
- [ ] `StartEngine` emits event with engine type and count
- [ ] Every empty catch block in InitAsync has `Signal.Warn` or `Signal.Error`
- [ ] Shutdown has an event
- [ ] All names are in Chinese, descriptive
- [ ] All detail fields include counts, paths, provider names, outcomes
