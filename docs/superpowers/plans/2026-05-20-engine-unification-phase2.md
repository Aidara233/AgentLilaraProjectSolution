# Engine Unification Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace EngineModule.BuildPromptSection pattern with IInjectProvider/IEngineLifecycle interfaces, introduce typed ChannelSignal/ModeBus, and migrate PluginLoader to multi-type constructor injection.

**Architecture:** Four foundation files (interfaces + types) → EngineModule/PluginLoader/ComponentHost adapted to new contracts → ChannelEngine/SystemEngine refactored to dual-source injection collection → 6 dead module files deleted, 4 WorkingTools upgraded to ITool+IInjectProvider dual interface.

**Tech Stack:** .NET 8, C#, AgentLilara.PluginSDK

---

### Task 1: IInjectProvider + InjectContext

**Files:**
- Create: `AgentCoreProcessor/Engine/Core/IInjectProvider.cs`

- [ ] **Step 1: Create the file**

```csharp
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>注入上下文。框架在调用注入钩子时传入，插件按需读取。</summary>
    public class InjectContext
    {
        public string Mode { get; init; } = "working";
        public int CurrentRound { get; init; }
        public int MaxRounds { get; init; }
        public int EstimatedTokens { get; init; }
    }

    /// <summary>插件/模块注入接口。框架在正确时机调用，注入什么由插件自行决定。</summary>
    public interface IInjectProvider
    {
        int InjectPriority { get; }
        Task<string?> BuildStartInjectAsync(InjectContext ctx);
        Task<string?> BuildRoundInjectAsync(InjectContext ctx);
    }
}
```

- [ ] **Step 2: Compile and verify**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Engine/Core/IInjectProvider.cs
git commit -m "feat: add IInjectProvider interface and InjectContext"
```

---

### Task 2: IEngineLifecycle

**Files:**
- Create: `AgentCoreProcessor/Engine/Core/IEngineLifecycle.cs`

- [ ] **Step 1: Create the file**

```csharp
using System;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>模块/插件生命周期接口。替代 EngineModule.Attach/Reset。</summary>
    public interface IEngineLifecycle
    {
        Task OnInitializeAsync(IServiceProvider services);
        Task OnShutdownAsync();
    }
}
```

- [ ] **Step 2: Compile and verify**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Engine/Core/IEngineLifecycle.cs
git commit -m "feat: add IEngineLifecycle interface"
```

---

### Task 3: ChannelSignal types

**Files:**
- Create: `AgentCoreProcessor/Engine/Core/ChannelSignal.cs`

- [ ] **Step 1: Create the file**

```csharp
using System.Collections.Generic;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine
{
    public abstract record ChannelSignal;

    /// <summary>新消息到达（用户发言、系统推送等）。</summary>
    public record NewMessageSignal(IncomingMessage Message, SessionContext Session) : ChannelSignal;

    /// <summary>EventBus 事件到达（委托完成、系统通知等）。</summary>
    public record BusEventSignal(EngineEvent Event) : ChannelSignal;

    /// <summary>压缩完成。包含新摘要 + 保留的对话历史。</summary>
    public record CompressionSignal(string Summary, List<Message> RetainedHistory) : ChannelSignal;

    /// <summary>模式切换（Express ↔ Working）。</summary>
    public record ModeSwitchSignal(string NewMode, string? Reason) : ChannelSignal;
}
```

- [ ] **Step 2: Compile and verify**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Engine/Core/ChannelSignal.cs
git commit -m "feat: add ChannelSignal typed records"
```

---

### Task 4: ModuleBus

**Files:**
- Create: `AgentCoreProcessor/Engine/Core/ModuleBus.cs`

- [ ] **Step 1: Create the file**

```csharp
using System;
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine
{
    /// <summary>每引擎独立的模块间通信总线。替代 ILoopBus + ComponentEventBus。</summary>
    public class ModuleBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new();
        private readonly object _lock = new();

        public IDisposable Subscribe<T>(Action<T> handler) where T : class
        {
            var type = typeof(T);
            lock (_lock)
            {
                if (!_handlers.TryGetValue(type, out var list))
                {
                    list = new List<Delegate>();
                    _handlers[type] = list;
                }
                list.Add(handler);
            }
            return new Unsubscriber<T>(this, handler);
        }

        public void Publish<T>(T message) where T : class
        {
            List<Delegate>? snapshot;
            lock (_lock)
            {
                if (_handlers.TryGetValue(typeof(T), out var list))
                    snapshot = new List<Delegate>(list);
                else
                    return;
            }
            foreach (var h in snapshot)
                ((Action<T>)h)(message);
        }

        private class Unsubscriber<T> : IDisposable where T : class
        {
            private readonly ModuleBus _bus;
            private readonly Action<T> _handler;
            public Unsubscriber(ModuleBus bus, Action<T> handler) { _bus = bus; _handler = handler; }
            public void Dispose()
            {
                lock (_bus._lock)
                {
                    if (_bus._handlers.TryGetValue(typeof(T), out var list))
                        list.Remove(_handler);
                }
            }
        }
    }
}
```

- [ ] **Step 2: Compile and verify**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: 0 errors

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Engine/Core/ModuleBus.cs
git commit -m "feat: add ModuleBus — per-engine isolated pub/sub"
```

---

### Task 5: EngineModule → IInjectProvider + IEngineLifecycle

**Files:**
- Modify: `AgentCoreProcessor/Engine/Worker/EngineModule.cs`
- Dependencies: Tasks 1, 2

- [ ] **Step 1: Update EngineModule base class**

Read the current file first (37 lines). Replace with:

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 模块基类。实现 IInjectProvider + IEngineLifecycle。
    /// BuildPromptSection / Attach / GetTools / Reset 标记 [Obsolete]，逐步移除。
    /// </summary>
    public abstract class EngineModule : IInjectProvider, IEngineLifecycle
    {
        public abstract string Name { get; }

        /// <summary>注入优先级（越小越靠前）。默认 50。</summary>
        public virtual int InjectPriority => 50;

        // ── IInjectProvider ──

        public virtual Task<string?> BuildStartInjectAsync(InjectContext ctx)
            => Task.FromResult(BuildPromptSection(MapMode(ctx.Mode)));

        public virtual Task<string?> BuildRoundInjectAsync(InjectContext ctx)
            => Task.FromResult<string?>(null);

        // ── IEngineLifecycle ──

        public virtual Task OnInitializeAsync(IServiceProvider services)
            => Task.CompletedTask;

        public virtual Task OnShutdownAsync()
        {
            Reset();
            return Task.CompletedTask;
        }

        // ── 废弃接口（保留编译兼容） ──

        [Obsolete("Use IEngineLifecycle.OnInitializeAsync")]
        public virtual void Attach(ILoopBus bus) { }

        [Obsolete("Override BuildStartInjectAsync instead")]
        public virtual string? BuildPromptSection(EngineMode mode) => null;

        [Obsolete("Use ITool directly")]
        public virtual IEnumerable<ITool> GetTools(EngineMode mode) => [];

        [Obsolete("Use IEngineLifecycle.OnShutdownAsync")]
        public virtual void Reset() { }

        // ── helpers ──

        private static EngineMode MapMode(string mode) => mode switch
        {
            "express" => EngineMode.Express,
            _ => EngineMode.Working
        };
    }
}
```

- [ ] **Step 2: Compile — expect only [Obsolete] warnings from callers**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: 0 errors, existing callers of BuildPromptSection/Attach will show CS0618 warnings (non-breaking)

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Engine/Worker/EngineModule.cs
git commit -m "refactor: EngineModule implements IInjectProvider + IEngineLifecycle"
```

---

### Task 6: PluginLoader — multi-type discovery + constructor injection

**Files:**
- Modify: `AgentCoreProcessor/Tool/Host/PluginLoader.cs`
- Dependencies: Tasks 1, 2, 3, 4

- [ ] **Step 1: Add IInjectProvider + IEngineLifecycle type discovery**

Read PluginLoader.cs. Add these after existing `DiscoverProviderTypes`:

```csharp
private static List<Type> DiscoverInjectProviderTypes(Assembly assembly)
{
    var iType = typeof(Engine.IInjectProvider);
    return assembly.GetExportedTypes()
        .Where(t => t.IsClass && !t.IsAbstract && iType.IsAssignableFrom(t))
        .ToList();
}

private static List<Type> DiscoverLifecycleTypes(Assembly assembly)
{
    var iType = typeof(Engine.IEngineLifecycle);
    return assembly.GetExportedTypes()
        .Where(t => t.IsClass && !t.IsAbstract && iType.IsAssignableFrom(t))
        .ToList();
}
```

- [ ] **Step 2: Add exposed type lists to PluginLoader**

Add properties:

```csharp
public IReadOnlyList<Type> InjectProviderTypes { get; private set; } = Array.Empty<Type>();
public IReadOnlyList<Type> LifecycleTypes { get; private set; } = Array.Empty<Type>();
```

- [ ] **Step 3: Add constructor injection helper**

```csharp
private static readonly Dictionary<Type, Func<IServiceProvider, object?>> _injectableResolvers = new()
{
    [typeof(Engine.EventBus)] = sp => sp.GetService(typeof(Engine.EventBus)),
    [typeof(Engine.ModuleBus)] = sp => sp.GetService(typeof(Engine.ModuleBus)),
    [typeof(Engine.Gate)] = sp => sp.GetService(typeof(Engine.Gate)),
    [typeof(AgentLilara.PluginSDK.IMemoryAccess)] = sp => sp.GetService(typeof(AgentLilara.PluginSDK.IMemoryAccess)),
};

public object? InstantiateWithInjection(Type type, IServiceProvider services)
{
    var ctors = type.GetConstructors()
        .OrderByDescending(c => c.GetParameters().Length);
    
    foreach (var ctor in ctors)
    {
        var parms = ctor.GetParameters();
        var args = new object?[parms.Length];
        bool ok = true;
        for (int i = 0; i < parms.Length; i++)
        {
            var pType = parms[i].ParameterType;
            if (_injectableResolvers.TryGetValue(pType, out var resolver))
                args[i] = resolver(services);
            else if (pType == typeof(IServiceProvider))
                args[i] = services;
            else
            {
                // Try IServiceProvider as fallback
                args[i] = services.GetService(pType);
                if (args[i] == null && !parms[i].IsOptional)
                {
                    ok = false;
                    break;
                }
            }
        }
        if (!ok) continue;
        
        try { return Activator.CreateInstance(type, args); }
        catch { continue; }
    }
    return null;
}
```

- [ ] **Step 4: Add delayed instantiation methods**

```csharp
public Engine.IInjectProvider? InstantiateInjectProvider(Type type, IServiceProvider engineServices)
    => InstantiateWithInjection(type, engineServices) as Engine.IInjectProvider;

public Engine.IEngineLifecycle? InstantiateLifecycle(Type type, IServiceProvider engineServices)
    => InstantiateWithInjection(type, engineServices) as Engine.IEngineLifecycle;
```

- [ ] **Step 5: Integrate discovery into LoadPlugin**

In `LoadPlugin`, add after `var earlyProviderTypes = DiscoverProviderTypes(assembly);`:

```csharp
var injectProviderTypes = DiscoverInjectProviderTypes(assembly);
var lifecycleTypes = DiscoverLifecycleTypes(assembly);
```

And collect them:

```csharp
entry.InjectProviderNames.AddRange(injectProviderTypes.Select(t => t.Name));
entry.LifecycleNames.AddRange(lifecycleTypes.Select(t => t.Name));
```

- [ ] **Step 6: Build aggregate lists after LoadAll**

Add at end of `LoadAll()`:

```csharp
InjectProviderTypes = loadedPlugins
    .SelectMany(p => DiscoverInjectProviderTypes(Assembly.LoadFrom(p.FilePath)))
    .ToList().AsReadOnly();
LifecycleTypes = loadedPlugins
    .SelectMany(p => DiscoverLifecycleTypes(Assembly.LoadFrom(p.FilePath)))
    .ToList().AsReadOnly();
```

- [ ] **Step 7: Update PluginEntry fields**

```csharp
public List<string> InjectProviderNames { get; set; } = new();
public List<string> LifecycleNames { get; set; } = new();
```

- [ ] **Step 8: Compile and verify**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: 0 errors

- [ ] **Step 9: Commit**

```bash
git add AgentCoreProcessor/Tool/Host/PluginLoader.cs
git commit -m "feat: PluginLoader — multi-type discovery + constructor injection for IInjectProvider/IEngineLifecycle"
```

---

### Task 7: ComponentHost — constructor injection, ComponentEventBus → ModuleBus

**Files:**
- Modify: `AgentCoreProcessor/Component/ComponentHost.cs`
- Modify: `AgentCoreProcessor/Component/LoopComponentContext.cs` (if ComponentEventBus references exist)
- Modify: `AgentCoreProcessor/Component/ComponentEventBus.cs` (mark deprecated or delete if no other consumers)
- Dependencies: Tasks 1, 4

- [ ] **Step 1: Check ComponentEventBus consumers**

Run: `grep -rn "ComponentEventBus" AgentCoreProcessor/ --include="*.cs"`

Expected: used in ComponentHost.cs and LoopComponentContext.cs (constructor). Delete ComponentEventBus.cs if no other consumers.

- [ ] **Step 2: Update ComponentHost constructor**

Change `ComponentEventBus` to `ModuleBus`:

```csharp
// Old:
// private readonly ComponentEventBus _eventBus;
// public ComponentHost(string loopId, string loopType, ComponentEventBus eventBus, ...)

// New:
private readonly ModuleBus _moduleBus;

public ComponentHost(
    string loopId,
    string loopType,
    ModuleBus moduleBus,
    IServiceProvider services,
    Action wakeLoop)
{
    _loopId = loopId;
    _loopType = loopType;
    _moduleBus = moduleBus;
    _services = services;
    _wakeLoop = wakeLoop;
    _config = ComponentConfig.Load();
}
```

- [ ] **Step 3: Update CreateInstance to use constructor injection**

Replace `Activator.CreateInstance(reg.Type)` with `InstantiateWithInjection`:

```csharp
private LoopComponentInstance? CreateInstance(ComponentRegistration reg)
{
    // Use PluginLoader's constructor injection
    var pluginLoader = _services.GetService(typeof(PluginLoader)) as Tool.Host.PluginLoader;
    var component = pluginLoader?.InstantiateWithInjection(reg.Type, _services) 
                    ?? Activator.CreateInstance(reg.Type);
    
    if (component is not ILoopComponent loopComponent) return null;
    // ... rest unchanged, use loopComponent instead of component
```

Make `InstantiateWithInjection` in PluginLoader `internal` instead of `public`.

- [ ] **Step 4: Update LoopComponentContext**

If `LoopComponentContext` takes `ComponentEventBus` in its constructor, change to `ModuleBus`:

```csharp
// Old:
// public LoopComponentContext(..., ComponentEventBus eventBus, ...)

// New:
public LoopComponentContext(..., ModuleBus moduleBus, ...)
```

Also update `_eventBus.RemoveLoop(_loopId)` in ShutdownAsync to `_moduleBus` cleanup (ModuleBus is per-engine, so no RemoveLoop needed — just clear subscriptions via Dispose).

- [ ] **Step 5: Update ChannelEngine/SystemEngine ComponentHost usage**

Search for `new ComponentHost(` or `ComponentEventBus` in engine files. If engines create ComponentHost with `new ComponentEventBus()`, change to pass `_moduleBus` instead.

- [ ] **Step 6: Compile and verify**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: 0 errors

- [ ] **Step 7: Commit**

```bash
git add AgentCoreProcessor/Component/ComponentHost.cs AgentCoreProcessor/Component/LoopComponentContext.cs
git commit -m "refactor: ComponentHost uses ModuleBus + constructor injection"
```

---

### Task 8: ChannelEngine — ChannelSignal buffer + dual-source IInjectProvider collection

**Files:**
- Modify: `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs`
- Dependencies: Tasks 1–7

**Key changes (file is ~1100 lines, target specific sections):**

- [ ] **Step 1: Replace ad-hoc buffer fields with ChannelSignal queue**

Remove fields: `activeBatch`, `interceptorInjections`, `escalationReason`.
Add field:

```csharp
private readonly ConcurrentQueue<ChannelSignal> _signalBuffer = new();
```

- [ ] **Step 2: Add ModuleBus field and inject provider list**

Add fields:

```csharp
private ModuleBus? _moduleBus;
private readonly List<IInjectProvider> _injectProviders = new();
```

- [ ] **Step 3: Initialize inject providers in RunAsync (before gate.RunAsync)**

Add after existing module setup:

```csharp
// Collect IInjectProviders: internal modules + plugin instances
_injectProviders.Clear();
_injectProviders.AddRange(modules);  // EngineModule : IInjectProvider
foreach (var type in ctx.PluginLoader.InjectProviderTypes)
{
    var provider = ctx.PluginLoader.InstantiateInjectProvider(type, _engineServices);
    if (provider != null)
        _injectProviders.Add(provider);
}
```

Where `_engineServices` is an `IServiceProvider` that resolves `ModuleBus`, `EventBus`, `Gate` for this engine (build it once from the engine's dependencies, e.g. via a simple `DictionaryServiceProvider` or by injecting `IServiceProvider` into ChannelEngine constructor).

- [ ] **Step 4: Update BuildStartInjectAsync**

Replace the manual formatting of activeBatch/interceptorInjections/escalationReason with:

```csharp
public Task<List<Message>?> BuildStartInjectAsync()
{
    var msgs = new List<Message>();
    var ctx2 = new InjectContext
    {
        Mode = isWorkingMode ? "working" : "express",
        CurrentRound = 0,
        MaxRounds = agentConfig.MaxRounds
    };
    foreach (var p in _injectProviders.OrderBy(p => p.InjectPriority))
    {
        var s = await p.BuildStartInjectAsync(ctx2);
        if (!string.IsNullOrEmpty(s))
            msgs.Add(new Message { Role = "user", Content = s });
    }
    return msgs.Count > 0 ? msgs : null;
}
```

- [ ] **Step 5: Update BuildRoundInjectAsync**

Drain `_signalBuffer`, format signals, then collect IInjectProvider round injections:

```csharp
public async Task<List<Message>?> BuildRoundInjectAsync()
{
    var msgs = new List<Message>();

    // Drain signal buffer
    while (_signalBuffer.TryDequeue(out var signal))
    {
        switch (signal)
        {
            case NewMessageSignal nms:
                var sb = new StringBuilder("<新消息>\n");
                sb.AppendLine($"{nms.Session.Person.Name ?? nms.Session.User.PlatformId}: {nms.Message.Content}");
                sb.Append("</新消息>");
                msgs.Add(new Message { Role = "user", Content = sb.ToString() });
                break;
            case BusEventSignal bes:
                msgs.Add(new Message { Role = "user", Content = $"[系统事件] {bes.Event.Type}" });
                break;
            case CompressionSignal cs:
                // Rebuild Agent with new summary + retained history
                contextSummary = cs.Summary;
                EnsureAgent();
                agent!.ClearHistory();
                agent.AddToHistory(new Message { Role = "user", Content = $"[上下文摘要]\n{cs.Summary}" });
                foreach (var msg in cs.RetainedHistory)
                    agent.AddToHistory(msg);
                break;
            case ModeSwitchSignal mss:
                isWorkingMode = mss.NewMode == "working";
                break;
        }
    }

    // IInjectProvider round injections
    var ctx2 = new InjectContext
    {
        Mode = isWorkingMode ? "working" : "express",
        CurrentRound = agent?.TotalRounds ?? 1,
        MaxRounds = agentConfig.MaxRounds,
        EstimatedTokens = agent?.History.Sum(m => (m.Content?.Length ?? 0)) / 3 ?? 0
    };
    foreach (var p in _injectProviders.OrderBy(p => p.InjectPriority))
    {
        var s = await p.BuildRoundInjectAsync(ctx2);
        if (!string.IsNullOrEmpty(s))
            msgs.Add(new Message { Role = "user", Content = s });
    }

    return msgs.Count > 0 ? msgs : null;
}
```

- [ ] **Step 6: Update external signal entry points**

Wherever `activeBatch` was set or `interceptorInjections` added, replace with `_signalBuffer.Enqueue(...)`:

```csharp
// Old: activeBatch = ...; interceptorInjections.Add(...);
// New:
_signalBuffer.Enqueue(new NewMessageSignal(msg, session));
_signalBuffer.Enqueue(new BusEventSignal(engineEvent));
```

- [ ] **Step 7: Compile and verify**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: 0 errors

- [ ] **Step 8: Commit**

```bash
git add AgentCoreProcessor/Engine/Worker/ChannelEngine.cs
git commit -m "refactor: ChannelEngine uses ChannelSignal buffer + dual-source IInjectProvider collection"
```

---

### Task 9: SystemEngine — same treatment

**Files:**
- Modify: `AgentCoreProcessor/Engine/System/SystemEngine.cs`
- Dependencies: Tasks 1–7

- [ ] **Step 1: Add ModuleBus + inject providers**

Same pattern as ChannelEngine Task 8 Steps 1–3, adapted for SystemEngine.

- [ ] **Step 2: Update BuildStartInjectAsync + BuildRoundInjectAsync**

Same flow — drain signals, collect IInjectProvider outputs, merge.

- [ ] **Step 3: Compile and verify**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/Engine/System/SystemEngine.cs
git commit -m "refactor: SystemEngine uses ChannelSignal buffer + dual-source IInjectProvider collection"
```

---

### Task 10: Delete B/C module files

**Files:**
- Delete: `AgentCoreProcessor/Engine/Worker/Modules/PinboardModule.cs`
- Delete: `AgentCoreProcessor/Engine/Worker/Modules/RetainListModule.cs`
- Delete: `AgentCoreProcessor/Engine/Worker/Modules/ThinkingNotesModule.cs`
- Delete: `AgentCoreProcessor/Engine/Worker/Modules/TaskListModule.cs`
- Delete: `AgentCoreProcessor/Engine/Worker/Modules/SpeakModule.cs`
- Delete: `AgentCoreProcessor/Engine/Worker/Modules/SignalDispatchModule.cs`
- Remove from: ChannelEngine.cs `modules` list initialization (where these are `new XxxModule(...)`)
- Dependencies: Tasks 8 (ChannelEngine no longer uses them directly)

- [ ] **Step 1: Remove module instantiations from ChannelEngine**

Search ChannelEngine.cs for `new PinboardModule`, `new RetainListModule`, `new ThinkingNotesModule`, `new TaskListModule`, `new SpeakModule`, `new SignalDispatchModule`, `new DelegationModule` — remove these lines from the `modules` list initialization.

- [ ] **Step 2: Delete the 6 module files**

```bash
rm AgentCoreProcessor/Engine/Worker/Modules/PinboardModule.cs
rm AgentCoreProcessor/Engine/Worker/Modules/RetainListModule.cs
rm AgentCoreProcessor/Engine/Worker/Modules/ThinkingNotesModule.cs
rm AgentCoreProcessor/Engine/Worker/Modules/TaskListModule.cs
rm AgentCoreProcessor/Engine/Worker/Modules/SpeakModule.cs
rm AgentCoreProcessor/Engine/Worker/Modules/SignalDispatchModule.cs
```

- [ ] **Step 3: Compile and verify**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: 0 errors

- [ ] **Step 4: Commit**

```bash
git add -u AgentCoreProcessor/Engine/Worker/ChannelEngine.cs
git add AgentCoreProcessor/Engine/Worker/Modules/PinboardModule.cs AgentCoreProcessor/Engine/Worker/Modules/RetainListModule.cs AgentCoreProcessor/Engine/Worker/Modules/ThinkingNotesModule.cs AgentCoreProcessor/Engine/Worker/Modules/TaskListModule.cs AgentCoreProcessor/Engine/Worker/Modules/SpeakModule.cs AgentCoreProcessor/Engine/Worker/Modules/SignalDispatchModule.cs
git commit -m "chore: delete 6 obsolete module files — replaced by IInjectProvider + ModuleBus"
```

---

### Task 11: Migrate 4 WorkingTools → ITool + IInjectProvider

**Files:**
- Modify: `Plugins/Plugin.WorkingTools/PinboardTool.cs`
- Modify: `Plugins/Plugin.WorkingTools/RetainListTool.cs`
- Modify: `Plugins/Plugin.WorkingTools/ThinkingNotesTool.cs`
- Modify: `Plugins/Plugin.WorkingTools/WorkingToolsComponent.cs` (if TaskListTool lives here)
- Dependencies: Tasks 1, 6

Each tool already has a `BuildSection()` method that reads its data file. The migration is: implement `IInjectProvider` on the class, map `BuildSection` → `BuildStartInjectAsync`.

- [ ] **Step 1: Update PinboardTool**

```csharp
// Change class declaration:
public class PinboardTool : ITool, AgentCoreProcessor.Engine.IInjectProvider

// Add IInjectProvider implementation:
public int InjectPriority => 55;
public Task<string?> BuildStartInjectAsync(AgentCoreProcessor.Engine.InjectContext ctx)
    => Task.FromResult(BuildSection());
public Task<string?> BuildRoundInjectAsync(AgentCoreProcessor.Engine.InjectContext ctx)
    => Task.FromResult<string?>(null);
```

- [ ] **Step 2: Update RetainListTool**

Same pattern, InjectPriority=60.

- [ ] **Step 3: Update ThinkingNotesTool**

Same pattern, InjectPriority=45.

- [ ] **Step 4: Update TaskListTool (in WorkingToolsComponent.cs)**

Same pattern, InjectPriority=50.

- [ ] **Step 5: Copy updated DLLs to output**

Run: `dotnet build Plugins/Plugin.WorkingTools/Plugin.WorkingTools.csproj`

Then copy output DLL to `AgentCoreProcessor/bin/Debug/net8.0/Plugins/` (or ensure build script / copy task handles it).

- [ ] **Step 6: Compile full solution**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: 0 errors

- [ ] **Step 7: Commit**

```bash
git add Plugins/Plugin.WorkingTools/PinboardTool.cs Plugins/Plugin.WorkingTools/RetainListTool.cs Plugins/Plugin.WorkingTools/ThinkingNotesTool.cs Plugins/Plugin.WorkingTools/WorkingToolsComponent.cs
git commit -m "refactor: WorkingTools implement ITool + IInjectProvider dual interface"
```

---

### Task 12: Integration verification + docs

**Files:**
- Modify: `CLAUDE.md` — update key paths
- Modify: `docs/architecture.md` — if IInjectProvider/ModuleBus references needed
- Dependencies: Tasks 1–11

- [ ] **Step 1: Full solution build**

```bash
taskkill /IM AgentCoreProcessor.exe /F 2>/dev/null; dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj
```
Expected: 0 errors, warnings ≤ pre-existing 51

- [ ] **Step 2: Test mode smoke test**

```bash
cd AgentCoreProcessor && dotnet run -- --test --delay 10
```

Expected: engines start without crashes, Express/Working cycle runs. Wait for 1–2 cycles, then Ctrl+C.

- [ ] **Step 3: Update CLAUDE.md key paths**

Replace the EngineModule reference with new paths:

```
- 注入接口：Engine/Core/IInjectProvider.cs（IInjectProvider + InjectContext）
- 生命周期：Engine/Core/IEngineLifecycle.cs
- 信号类型：Engine/Core/ChannelSignal.cs（NewMessageSignal/BusEventSignal/CompressionSignal/ModeSwitchSignal）
- 模块总线：Engine/Core/ModuleBus.cs（每引擎独立，替代 ILoopBus + ComponentEventBus）
```

- [ ] **Step 4: Commit**

```bash
git add CLAUDE.md docs/
git commit -m "docs: update key paths for Phase 2 — IInjectProvider, ChannelSignal, ModuleBus"
```

- [ ] **Step 5: Tag Phase 2 completion**

```bash
git tag phase2-engine-unification-modules
```
