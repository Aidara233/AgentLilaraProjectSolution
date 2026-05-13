# Component 系统实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将"工具 + 引擎模块"二元架构统一为 Component 模型，所有工具归属 Component，支持生命周期钩子、事件订阅、Prompt 注入。

**Architecture:** SDK 定义 IGlobalComponent/ILoopComponent 接口 + Attribute 声明。主程序 ComponentRegistry 管理类型注册，ComponentHost 管理实例生命周期和事件路由。PluginLoader 扫描 [Component] 标记的类型。ChannelEngine/SystemEngine 通过 ComponentHost 替代硬编码 EngineModule。

**Tech Stack:** .NET 8 / C# / AgentLilara.PluginSDK / AssemblyLoadContext

---

## 文件结构

### SDK 新增文件
- `AgentLilara.PluginSDK/ILoopComponent.cs` — Loop Component 接口
- `AgentLilara.PluginSDK/IGlobalComponent.cs` — Global Component 接口
- `AgentLilara.PluginSDK/ILoopComponentContext.cs` — Loop 上下文接口
- `AgentLilara.PluginSDK/IGlobalComponentContext.cs` — Global 上下文接口
- `AgentLilara.PluginSDK/ComponentMeta.cs` — 元数据类
- `AgentLilara.PluginSDK/ComponentAttribute.cs` — Attribute 声明（Component/LoopApplicability/ToolVisibility）
- `AgentLilara.PluginSDK/ComponentEnums.cs` — 枚举（ComponentScope/Applicability/Visibility/ShutdownReason/InitReason）
- `AgentLilara.PluginSDK/ShutdownResponse.cs` — 关闭响应 record
- `AgentLilara.PluginSDK/LoopInfo.cs` — 循环信息 record
- `AgentLilara.PluginSDK/Events/BuiltInEvents.cs` — 内置事件类型
- `AgentLilara.PluginSDK/ComponentBase.cs` — 默认空实现基类（减少样板代码）

### 主程序新增文件
- `AgentCoreProcessor/Component/ComponentRegistry.cs` — 类型注册表（扫描结果存储）
- `AgentCoreProcessor/Component/ComponentHost.cs` — 实例管理（生命周期 + 事件路由 + 工具收集）
- `AgentCoreProcessor/Component/GlobalComponentContext.cs` — IGlobalComponentContext 实现
- `AgentCoreProcessor/Component/LoopComponentContext.cs` — ILoopComponentContext 实现
- `AgentCoreProcessor/Component/ComponentEventBus.cs` — 事件路由（Local + Global）
- `AgentCoreProcessor/Component/ComponentConfig.cs` — 配置加载（启用/禁用/超时）

### 主程序修改文件
- `AgentCoreProcessor/Tool/Host/PluginLoader.cs` — 增加 Component 扫描路径
- `AgentCoreProcessor/Engine/Core/MasterEngine.cs` — 集成 ComponentRegistry + Global Component 初始化
- `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs` — 用 ComponentHost 替代 EngineModule
- `AgentCoreProcessor/Engine/System/SystemEngine.cs` — 同上

### 插件迁移
- `Plugins/Plugin.BasicTools/BasicToolsComponent.cs` — 包装为 IGlobalComponent
- `Plugins/Plugin.WorkingTools/WorkingToolsComponent.cs` — 拆为 ILoopComponent（有 prompt 注入）
- `Plugins/Plugin.MemoryTools/MemoryToolsComponent.cs` — 包装为 IGlobalComponent
- `Plugins/Plugin.FileTools/FileToolsComponent.cs` — 包装为 IGlobalComponent

---

## Task 1: SDK 接口 — 枚举和基础类型

**Files:**
- Create: `AgentLilara.PluginSDK/ComponentEnums.cs`
- Create: `AgentLilara.PluginSDK/ShutdownResponse.cs`
- Create: `AgentLilara.PluginSDK/ComponentMeta.cs`
- Create: `AgentLilara.PluginSDK/LoopInfo.cs`
- Create: `AgentLilara.PluginSDK/Events/BuiltInEvents.cs`

- [ ] **Step 1: 创建 ComponentEnums.cs**

```csharp
// AgentLilara.PluginSDK/ComponentEnums.cs
namespace AgentLilara.PluginSDK;

public enum ComponentScope { Global, Loop }
public enum Applicability { Enabled, Disabled, NotApplicable }
public enum Visibility { AlwaysVisible, FollowState, AlwaysHidden }
public enum ShutdownReason { Destroy, Reload }
public enum InitReason { Fresh, Reload }
```

- [ ] **Step 2: 创建 ShutdownResponse.cs**

```csharp
// AgentLilara.PluginSDK/ShutdownResponse.cs
namespace AgentLilara.PluginSDK;

public record ShutdownResponse(bool Allow, string? Reason = null)
{
    public static ShutdownResponse Ok => new(true);
    public static ShutdownResponse NotReady(string reason) => new(false, reason);
}
```

- [ ] **Step 3: 创建 ComponentMeta.cs**

```csharp
// AgentLilara.PluginSDK/ComponentMeta.cs
namespace AgentLilara.PluginSDK;

public class ComponentMeta
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool DefaultEnabled { get; init; } = true;
    public int PromptPriority { get; init; } = 50;
}
```

- [ ] **Step 4: 创建 LoopInfo.cs**

```csharp
// AgentLilara.PluginSDK/LoopInfo.cs
namespace AgentLilara.PluginSDK;

public record LoopInfo(string LoopId, string LoopType);
```

- [ ] **Step 5: 创建 Events/BuiltInEvents.cs**

```csharp
// AgentLilara.PluginSDK/Events/BuiltInEvents.cs
namespace AgentLilara.PluginSDK.Events;

public record MessageReceived(string LoopId, string SenderId, string Content);
public record LoopActivated(string LoopId, string Reason);
public record LoopPausing(string LoopId);
public record TaskArrived(string TaskId, string Description);
public record ComponentStateChanged(string ComponentName, bool IsEnabled, string LoopId);
```

- [ ] **Step 6: 编译验证**

Run: `dotnet build AgentLilara.PluginSDK/AgentLilara.PluginSDK.csproj`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add AgentLilara.PluginSDK/ComponentEnums.cs AgentLilara.PluginSDK/ShutdownResponse.cs AgentLilara.PluginSDK/ComponentMeta.cs AgentLilara.PluginSDK/LoopInfo.cs AgentLilara.PluginSDK/Events/
git commit -m "feat(sdk): add Component system enums and base types"
```

---

## Task 2: SDK 接口 — Attribute 声明

**Files:**
- Create: `AgentLilara.PluginSDK/ComponentAttribute.cs`

- [ ] **Step 1: 创建 ComponentAttribute.cs**

```csharp
// AgentLilara.PluginSDK/ComponentAttribute.cs
namespace AgentLilara.PluginSDK;

[AttributeUsage(AttributeTargets.Class)]
public class ComponentAttribute : Attribute
{
    public required string Name { get; set; }
    public ComponentScope Scope { get; set; } = ComponentScope.Global;
}

[AttributeUsage(AttributeTargets.Class)]
public class LoopApplicabilityAttribute : Attribute
{
    public Applicability Channel { get; set; } = Applicability.Enabled;
    public Applicability System { get; set; } = Applicability.Enabled;
}

[AttributeUsage(AttributeTargets.Class)]
public class ToolVisibilityAttribute : Attribute
{
    public Visibility Default { get; set; } = Visibility.FollowState;
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build AgentLilara.PluginSDK/AgentLilara.PluginSDK.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add AgentLilara.PluginSDK/ComponentAttribute.cs
git commit -m "feat(sdk): add Component/LoopApplicability/ToolVisibility attributes"
```

---

## Task 3: SDK 接口 — Context 接口

**Files:**
- Create: `AgentLilara.PluginSDK/ILoopComponentContext.cs`
- Create: `AgentLilara.PluginSDK/IGlobalComponentContext.cs`

- [ ] **Step 1: 创建 ILoopComponentContext.cs**

```csharp
// AgentLilara.PluginSDK/ILoopComponentContext.cs
namespace AgentLilara.PluginSDK;

public interface ILoopComponentContext
{
    string LoopId { get; }
    string LoopType { get; }

    bool IsEnabled { get; }
    void Enable();
    void Disable();

    void WakeLoop();

    void PublishLocal<TEvent>(TEvent e) where TEvent : class;
    void PublishGlobal<TEvent>(TEvent e) where TEvent : class;
    void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
    void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;

    T? GetService<T>() where T : class;
    IPluginStorage Storage { get; }
}
```

- [ ] **Step 2: 创建 IGlobalComponentContext.cs**

```csharp
// AgentLilara.PluginSDK/IGlobalComponentContext.cs
namespace AgentLilara.PluginSDK;

public interface IGlobalComponentContext
{
    bool IsEnabled { get; }
    void Enable();
    void Disable();

    void WakeLoop(string loopId);

    void PublishGlobal<TEvent>(TEvent e) where TEvent : class;
    void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
    void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;

    T? GetService<T>() where T : class;
    IPluginStorage Storage { get; }
}
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build AgentLilara.PluginSDK/AgentLilara.PluginSDK.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add AgentLilara.PluginSDK/ILoopComponentContext.cs AgentLilara.PluginSDK/IGlobalComponentContext.cs
git commit -m "feat(sdk): add ILoopComponentContext and IGlobalComponentContext"
```

---

## Task 4: SDK 接口 — Component 接口 + 基类

**Files:**
- Create: `AgentLilara.PluginSDK/ILoopComponent.cs`
- Create: `AgentLilara.PluginSDK/IGlobalComponent.cs`
- Create: `AgentLilara.PluginSDK/ComponentBase.cs`

- [ ] **Step 1: 创建 ILoopComponent.cs**

```csharp
// AgentLilara.PluginSDK/ILoopComponent.cs
namespace AgentLilara.PluginSDK;

public interface ILoopComponent
{
    ComponentMeta Meta { get; }
    IEnumerable<ITool> Tools { get; }

    Task OnInitAsync(ILoopComponentContext context, InitReason reason);
    Task<ShutdownResponse> OnShutdownRequestedAsync(ShutdownReason reason);
    Task OnShutdownAsync(ShutdownReason reason);

    Task OnEnabledAsync();
    Task OnDisabledAsync();

    Task OnActivatedAsync();
    Task OnPauseAsync();
    Task OnBeforeInvokeAsync();
    Task OnAfterInvokeAsync();

    string? BuildPromptSection();
}
```

- [ ] **Step 2: 创建 IGlobalComponent.cs**

```csharp
// AgentLilara.PluginSDK/IGlobalComponent.cs
namespace AgentLilara.PluginSDK;

public interface IGlobalComponent
{
    ComponentMeta Meta { get; }
    IEnumerable<ITool> Tools { get; }

    Task OnInitAsync(IGlobalComponentContext context, InitReason reason);
    Task<ShutdownResponse> OnShutdownRequestedAsync(ShutdownReason reason);
    Task OnShutdownAsync(ShutdownReason reason);

    Task OnEnabledAsync();
    Task OnDisabledAsync();

    string? BuildPromptSection(LoopInfo caller);
}
```

- [ ] **Step 3: 创建 ComponentBase.cs（默认空实现，减少样板）**

```csharp
// AgentLilara.PluginSDK/ComponentBase.cs
namespace AgentLilara.PluginSDK;

public abstract class LoopComponentBase : ILoopComponent
{
    public abstract ComponentMeta Meta { get; }
    public abstract IEnumerable<ITool> Tools { get; }

    public virtual Task OnInitAsync(ILoopComponentContext context, InitReason reason) => Task.CompletedTask;
    public virtual Task<ShutdownResponse> OnShutdownRequestedAsync(ShutdownReason reason) => Task.FromResult(ShutdownResponse.Ok);
    public virtual Task OnShutdownAsync(ShutdownReason reason) => Task.CompletedTask;
    public virtual Task OnEnabledAsync() => Task.CompletedTask;
    public virtual Task OnDisabledAsync() => Task.CompletedTask;
    public virtual Task OnActivatedAsync() => Task.CompletedTask;
    public virtual Task OnPauseAsync() => Task.CompletedTask;
    public virtual Task OnBeforeInvokeAsync() => Task.CompletedTask;
    public virtual Task OnAfterInvokeAsync() => Task.CompletedTask;
    public virtual string? BuildPromptSection() => null;
}

public abstract class GlobalComponentBase : IGlobalComponent
{
    public abstract ComponentMeta Meta { get; }
    public abstract IEnumerable<ITool> Tools { get; }

    public virtual Task OnInitAsync(IGlobalComponentContext context, InitReason reason) => Task.CompletedTask;
    public virtual Task<ShutdownResponse> OnShutdownRequestedAsync(ShutdownReason reason) => Task.FromResult(ShutdownResponse.Ok);
    public virtual Task OnShutdownAsync(ShutdownReason reason) => Task.CompletedTask;
    public virtual Task OnEnabledAsync() => Task.CompletedTask;
    public virtual Task OnDisabledAsync() => Task.CompletedTask;
    public virtual string? BuildPromptSection(LoopInfo caller) => null;
}
```

- [ ] **Step 4: 编译验证**

Run: `dotnet build AgentLilara.PluginSDK/AgentLilara.PluginSDK.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add AgentLilara.PluginSDK/ILoopComponent.cs AgentLilara.PluginSDK/IGlobalComponent.cs AgentLilara.PluginSDK/ComponentBase.cs
git commit -m "feat(sdk): add ILoopComponent, IGlobalComponent interfaces and base classes"
```

---

## Task 5: 主程序 — ComponentRegistry（类型注册表）

**Files:**
- Create: `AgentCoreProcessor/Component/ComponentRegistry.cs`

- [ ] **Step 1: 创建 ComponentRegistry.cs**

```csharp
// AgentCoreProcessor/Component/ComponentRegistry.cs
using System.Collections.Concurrent;
using System.Reflection;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal record ComponentRegistration(
    Type Type,
    ComponentScope Scope,
    Applicability ChannelApplicability,
    Applicability SystemApplicability,
    Assembly SourceAssembly);

internal static class ComponentRegistry
{
    private static readonly ConcurrentDictionary<string, ComponentRegistration> _registrations = new();

    public static bool Register(Type type)
    {
        var attr = type.GetCustomAttribute<ComponentAttribute>();
        if (attr == null) return false;

        var loopAttr = type.GetCustomAttribute<LoopApplicabilityAttribute>();

        var reg = new ComponentRegistration(
            Type: type,
            Scope: attr.Scope,
            ChannelApplicability: loopAttr?.Channel ?? Applicability.Enabled,
            SystemApplicability: loopAttr?.System ?? Applicability.Enabled,
            SourceAssembly: type.Assembly);

        return _registrations.TryAdd(attr.Name, reg);
    }

    public static void Unregister(string name) => _registrations.TryRemove(name, out _);

    public static ComponentRegistration? Get(string name)
    {
        _registrations.TryGetValue(name, out var reg);
        return reg;
    }

    public static IEnumerable<ComponentRegistration> GetAll() => _registrations.Values;

    public static IEnumerable<ComponentRegistration> GetGlobals() =>
        _registrations.Values.Where(r => r.Scope == ComponentScope.Global);

    public static IEnumerable<ComponentRegistration> GetLoopComponents(string loopType)
    {
        return _registrations.Values.Where(r =>
        {
            if (r.Scope != ComponentScope.Loop) return false;
            var applicability = loopType switch
            {
                "channel" => r.ChannelApplicability,
                "system" => r.SystemApplicability,
                _ => Applicability.Enabled
            };
            return applicability != Applicability.NotApplicable;
        });
    }

    public static void Clear() => _registrations.Clear();
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Component/ComponentRegistry.cs
git commit -m "feat: add ComponentRegistry for type-level component management"
```

---

## Task 6: 主程序 — ComponentEventBus（事件路由）

**Files:**
- Create: `AgentCoreProcessor/Component/ComponentEventBus.cs`

- [ ] **Step 1: 创建 ComponentEventBus.cs**

```csharp
// AgentCoreProcessor/Component/ComponentEventBus.cs
using System.Collections.Concurrent;

namespace AgentCoreProcessor.Component;

internal class ComponentEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _globalHandlers = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Type, List<Delegate>>> _localHandlers = new();

    public void SubscribeGlobal<TEvent>(Func<TEvent, Task> handler) where TEvent : class
    {
        var list = _globalHandlers.GetOrAdd(typeof(TEvent), _ => new List<Delegate>());
        lock (list) { list.Add(handler); }
    }

    public void UnsubscribeGlobal<TEvent>(Func<TEvent, Task> handler) where TEvent : class
    {
        if (_globalHandlers.TryGetValue(typeof(TEvent), out var list))
            lock (list) { list.Remove(handler); }
    }

    public void SubscribeLocal<TEvent>(string loopId, Func<TEvent, Task> handler) where TEvent : class
    {
        var loopHandlers = _localHandlers.GetOrAdd(loopId, _ => new());
        var list = loopHandlers.GetOrAdd(typeof(TEvent), _ => new List<Delegate>());
        lock (list) { list.Add(handler); }
    }

    public void UnsubscribeLocal<TEvent>(string loopId, Func<TEvent, Task> handler) where TEvent : class
    {
        if (_localHandlers.TryGetValue(loopId, out var loopHandlers))
            if (loopHandlers.TryGetValue(typeof(TEvent), out var list))
                lock (list) { list.Remove(handler); }
    }

    public async Task PublishGlobalAsync<TEvent>(TEvent e) where TEvent : class
    {
        // 发送到所有 global 订阅者
        if (_globalHandlers.TryGetValue(typeof(TEvent), out var globalList))
        {
            List<Delegate> snapshot;
            lock (globalList) { snapshot = globalList.ToList(); }
            foreach (var handler in snapshot)
            {
                try { await ((Func<TEvent, Task>)handler)(e); }
                catch (Exception ex) { LogError(ex, typeof(TEvent).Name, "global"); }
            }
        }

        // 发送到所有 loop 的 local 订阅者（global 事件对所有人可见）
        foreach (var (loopId, loopHandlers) in _localHandlers)
        {
            if (loopHandlers.TryGetValue(typeof(TEvent), out var localList))
            {
                List<Delegate> snapshot;
                lock (localList) { snapshot = localList.ToList(); }
                foreach (var handler in snapshot)
                {
                    try { await ((Func<TEvent, Task>)handler)(e); }
                    catch (Exception ex) { LogError(ex, typeof(TEvent).Name, loopId); }
                }
            }
        }
    }

    public async Task PublishLocalAsync<TEvent>(string loopId, TEvent e) where TEvent : class
    {
        if (!_localHandlers.TryGetValue(loopId, out var loopHandlers)) return;
        if (!loopHandlers.TryGetValue(typeof(TEvent), out var list)) return;

        List<Delegate> snapshot;
        lock (list) { snapshot = list.ToList(); }
        foreach (var handler in snapshot)
        {
            try { await ((Func<TEvent, Task>)handler)(e); }
            catch (Exception ex) { LogError(ex, typeof(TEvent).Name, loopId); }
        }
    }

    public void RemoveLoop(string loopId) => _localHandlers.TryRemove(loopId, out _);

    private static void LogError(Exception ex, string eventType, string scope)
    {
        Util.FrameworkLogger.Log($"[ComponentEventBus] Handler error for {eventType} in {scope}: {ex.Message}");
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Component/ComponentEventBus.cs
git commit -m "feat: add ComponentEventBus with Local/Global event routing"
```

---

## Task 7: 主程序 — Context 实现

**Files:**
- Create: `AgentCoreProcessor/Component/LoopComponentContext.cs`
- Create: `AgentCoreProcessor/Component/GlobalComponentContext.cs`

- [ ] **Step 1: 创建 LoopComponentContext.cs**

```csharp
// AgentCoreProcessor/Component/LoopComponentContext.cs
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal class LoopComponentContext : ILoopComponentContext
{
    private readonly string _loopId;
    private readonly string _loopType;
    private readonly ComponentEventBus _eventBus;
    private readonly IServiceProvider _services;
    private readonly IPluginStorage _storage;
    private readonly Action _wakeLoop;
    private readonly Action<string, bool> _setEnabled;

    private bool _isEnabled;

    public LoopComponentContext(
        string loopId,
        string loopType,
        ComponentEventBus eventBus,
        IServiceProvider services,
        IPluginStorage storage,
        Action wakeLoop,
        Action<string, bool> setEnabled,
        bool initialEnabled)
    {
        _loopId = loopId;
        _loopType = loopType;
        _eventBus = eventBus;
        _services = services;
        _storage = storage;
        _wakeLoop = wakeLoop;
        _setEnabled = setEnabled;
        _isEnabled = initialEnabled;
    }

    public string LoopId => _loopId;
    public string LoopType => _loopType;
    public bool IsEnabled => _isEnabled;
    public IPluginStorage Storage => _storage;

    public void Enable()
    {
        if (_isEnabled) return;
        _isEnabled = true;
        _setEnabled(_loopId, true);
    }

    public void Disable()
    {
        if (!_isEnabled) return;
        _isEnabled = false;
        _setEnabled(_loopId, false);
    }

    public void WakeLoop() => _wakeLoop();

    public void PublishLocal<TEvent>(TEvent e) where TEvent : class
        => _ = _eventBus.PublishLocalAsync(_loopId, e);

    public void PublishGlobal<TEvent>(TEvent e) where TEvent : class
        => _ = _eventBus.PublishGlobalAsync(e);

    public void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
        => _eventBus.SubscribeLocal<TEvent>(_loopId, handler);

    public void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
        => _eventBus.UnsubscribeLocal<TEvent>(_loopId, handler);

    public T? GetService<T>() where T : class
        => _services.GetService(typeof(T)) as T;

    internal void SetEnabledDirect(bool enabled) => _isEnabled = enabled;
}
```

- [ ] **Step 2: 创建 GlobalComponentContext.cs**

```csharp
// AgentCoreProcessor/Component/GlobalComponentContext.cs
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal class GlobalComponentContext : IGlobalComponentContext
{
    private readonly ComponentEventBus _eventBus;
    private readonly IServiceProvider _services;
    private readonly IPluginStorage _storage;
    private readonly Action<string> _wakeLoop;
    private readonly Action<bool> _setEnabled;

    private bool _isEnabled;

    public GlobalComponentContext(
        ComponentEventBus eventBus,
        IServiceProvider services,
        IPluginStorage storage,
        Action<string> wakeLoop,
        Action<bool> setEnabled,
        bool initialEnabled)
    {
        _eventBus = eventBus;
        _services = services;
        _storage = storage;
        _wakeLoop = wakeLoop;
        _setEnabled = setEnabled;
        _isEnabled = initialEnabled;
    }

    public bool IsEnabled => _isEnabled;
    public IPluginStorage Storage => _storage;

    public void Enable()
    {
        if (_isEnabled) return;
        _isEnabled = true;
        _setEnabled(true);
    }

    public void Disable()
    {
        if (!_isEnabled) return;
        _isEnabled = false;
        _setEnabled(false);
    }

    public void WakeLoop(string loopId) => _wakeLoop(loopId);

    public void PublishGlobal<TEvent>(TEvent e) where TEvent : class
        => _ = _eventBus.PublishGlobalAsync(e);

    public void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
        => _eventBus.SubscribeGlobal(handler);

    public void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class
        => _eventBus.UnsubscribeGlobal(handler);

    public T? GetService<T>() where T : class
        => _services.GetService(typeof(T)) as T;

    internal void SetEnabledDirect(bool enabled) => _isEnabled = enabled;
}
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/Component/LoopComponentContext.cs AgentCoreProcessor/Component/GlobalComponentContext.cs
git commit -m "feat: add LoopComponentContext and GlobalComponentContext implementations"
```

---

## Task 8: 主程序 — ComponentConfig（配置加载）

**Files:**
- Create: `AgentCoreProcessor/Component/ComponentConfig.cs`

- [ ] **Step 1: 创建 ComponentConfig.cs**

```csharp
// AgentCoreProcessor/Component/ComponentConfig.cs
using System.Text.Json;
using AgentCoreProcessor.Config;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal class ComponentConfigEntry
{
    public bool? Enabled { get; set; }
    public Visibility? ToolVisibility { get; set; }
}

internal class ComponentConfig
{
    private static string ConfigPath =>
        Path.Combine(PathConfig.StoragePath, "Engine", "ComponentConfig.json");

    public int ShutdownTimeoutMs { get; set; } = 30000;
    public Dictionary<string, ComponentConfigEntry> Components { get; set; } = new();

    private static ComponentConfig? _cached;

    public static ComponentConfig Load()
    {
        if (_cached != null) return _cached;

        if (!File.Exists(ConfigPath))
        {
            _cached = new ComponentConfig();
            return _cached;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            _cached = JsonSerializer.Deserialize<ComponentConfig>(json) ?? new();
        }
        catch
        {
            _cached = new ComponentConfig();
        }
        return _cached;
    }

    public static void Invalidate() => _cached = null;

    public bool IsEnabled(string componentName, bool defaultEnabled)
    {
        if (Components.TryGetValue(componentName, out var entry) && entry.Enabled.HasValue)
            return entry.Enabled.Value;
        return defaultEnabled;
    }

    public Visibility GetVisibility(string componentName, Visibility defaultVisibility)
    {
        if (Components.TryGetValue(componentName, out var entry) && entry.ToolVisibility.HasValue)
            return entry.ToolVisibility.Value;
        return defaultVisibility;
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Component/ComponentConfig.cs
git commit -m "feat: add ComponentConfig for component enable/disable/timeout settings"
```

---

## Task 9: 主程序 — ComponentHost（实例管理 + 生命周期）

**Files:**
- Create: `AgentCoreProcessor/Component/ComponentHost.cs`

- [ ] **Step 1: 创建 ComponentHost.cs**

```csharp
// AgentCoreProcessor/Component/ComponentHost.cs
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

/// <summary>
/// 管理一个循环内所有 Component 实例的生命周期、工具收集、Prompt 收集。
/// 每个 ChannelEngine/SystemEngine 持有一个 ComponentHost。
/// </summary>
internal class ComponentHost
{
    private readonly string _loopId;
    private readonly string _loopType;
    private readonly ComponentEventBus _eventBus;
    private readonly IServiceProvider _services;
    private readonly Action _wakeLoop;

    private readonly List<LoopComponentInstance> _loopComponents = new();
    private readonly ComponentConfig _config;

    public ComponentHost(
        string loopId,
        string loopType,
        ComponentEventBus eventBus,
        IServiceProvider services,
        Action wakeLoop)
    {
        _loopId = loopId;
        _loopType = loopType;
        _eventBus = eventBus;
        _services = services;
        _wakeLoop = wakeLoop;
        _config = ComponentConfig.Load();
    }

    public async Task InitAsync()
    {
        var registrations = ComponentRegistry.GetLoopComponents(_loopType);

        foreach (var reg in registrations)
        {
            try
            {
                var instance = CreateInstance(reg);
                if (instance == null) continue;
                _loopComponents.Add(instance);
                await instance.Component.OnInitAsync(instance.Context, InitReason.Fresh);
            }
            catch (Exception ex)
            {
                Util.FrameworkLogger.Log($"[ComponentHost] Init failed for {reg.Type.Name}: {ex.Message}");
            }
        }
    }

    public IEnumerable<ITool> GetVisibleTools()
    {
        foreach (var inst in _loopComponents)
        {
            if (!ShouldShowTools(inst)) continue;
            foreach (var tool in inst.Component.Tools)
                yield return tool;
        }
    }

    public List<string> BuildPromptSections()
    {
        var sections = new List<(int priority, string content)>();

        foreach (var inst in _loopComponents)
        {
            if (!inst.Context.IsEnabled) continue;
            var section = inst.Component.BuildPromptSection();
            if (section != null)
                sections.Add((inst.Component.Meta.PromptPriority, section));
        }

        return sections
            .OrderBy(s => s.priority)
            .Select(s => s.content)
            .ToList();
    }

    public async Task OnActivatedAsync()
    {
        foreach (var inst in _loopComponents.Where(i => i.Context.IsEnabled))
        {
            try { await inst.Component.OnActivatedAsync(); }
            catch (Exception ex) { LogError(inst, "OnActivated", ex); }
        }
    }

    public async Task OnPauseAsync()
    {
        foreach (var inst in _loopComponents.Where(i => i.Context.IsEnabled))
        {
            try { await inst.Component.OnPauseAsync(); }
            catch (Exception ex) { LogError(inst, "OnPause", ex); }
        }
    }

    public async Task OnBeforeInvokeAsync()
    {
        foreach (var inst in _loopComponents.Where(i => i.Context.IsEnabled))
        {
            try { await inst.Component.OnBeforeInvokeAsync(); }
            catch (Exception ex) { LogError(inst, "OnBeforeInvoke", ex); }
        }
    }

    public async Task OnAfterInvokeAsync()
    {
        foreach (var inst in _loopComponents.Where(i => i.Context.IsEnabled))
        {
            try { await inst.Component.OnAfterInvokeAsync(); }
            catch (Exception ex) { LogError(inst, "OnAfterInvoke", ex); }
        }
    }

    public async Task EnableComponentAsync(string name)
    {
        var inst = _loopComponents.FirstOrDefault(i => i.Component.Meta.Name == name);
        if (inst == null || inst.Context.IsEnabled) return;
        inst.Context.SetEnabledDirect(true);
        try { await inst.Component.OnEnabledAsync(); }
        catch (Exception ex) { LogError(inst, "OnEnabled", ex); }
    }

    public async Task DisableComponentAsync(string name)
    {
        var inst = _loopComponents.FirstOrDefault(i => i.Component.Meta.Name == name);
        if (inst == null || !inst.Context.IsEnabled) return;
        inst.Context.SetEnabledDirect(false);
        try { await inst.Component.OnDisabledAsync(); }
        catch (Exception ex) { LogError(inst, "OnDisabled", ex); }
    }

    public async Task ShutdownAsync(ShutdownReason reason)
    {
        var timeout = _config.ShutdownTimeoutMs;

        // Phase 1: 等待就绪
        using var cts = new CancellationTokenSource(timeout);
        var tasks = _loopComponents.Select(async inst =>
        {
            try { return await inst.Component.OnShutdownRequestedAsync(reason); }
            catch { return ShutdownResponse.Ok; }
        });

        try { await Task.WhenAll(tasks).WaitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            Util.FrameworkLogger.Log($"[ComponentHost] Shutdown timeout for loop {_loopId}, forcing phase 2");
        }

        // Phase 2: 最终关闭
        foreach (var inst in _loopComponents)
        {
            try { await inst.Component.OnShutdownAsync(reason).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { LogError(inst, "OnShutdown", ex); }
        }

        _eventBus.RemoveLoop(_loopId);
        _loopComponents.Clear();
    }

    public IReadOnlyList<LoopComponentInstance> Instances => _loopComponents;

    private LoopComponentInstance? CreateInstance(ComponentRegistration reg)
    {
        var component = (ILoopComponent?)Activator.CreateInstance(reg.Type);
        if (component == null) return null;

        var defaultEnabled = _config.IsEnabled(component.Meta.Name, component.Meta.DefaultEnabled);
        var storage = new ComponentStorage(component.Meta.Name, _loopId);

        var context = new LoopComponentContext(
            _loopId, _loopType, _eventBus, _services, storage,
            _wakeLoop,
            (_, enabled) =>
            {
                if (enabled) _ = EnableComponentAsync(component.Meta.Name);
                else _ = DisableComponentAsync(component.Meta.Name);
            },
            defaultEnabled);

        return new LoopComponentInstance(component, context, reg);
    }

    private bool ShouldShowTools(LoopComponentInstance inst)
    {
        var visibility = _config.GetVisibility(
            inst.Component.Meta.Name,
            inst.Registration.Type.GetCustomAttribute<ToolVisibilityAttribute>()?.Default ?? Visibility.FollowState);

        return visibility switch
        {
            Visibility.AlwaysVisible => true,
            Visibility.AlwaysHidden => false,
            Visibility.FollowState => inst.Context.IsEnabled,
            _ => inst.Context.IsEnabled
        };
    }

    private static void LogError(LoopComponentInstance inst, string hook, Exception ex)
    {
        Util.FrameworkLogger.Log($"[ComponentHost] {hook} error in {inst.Component.Meta.Name}: {ex.Message}");
    }
}

internal record LoopComponentInstance(
    ILoopComponent Component,
    LoopComponentContext Context,
    ComponentRegistration Registration);

internal class ComponentStorage : IPluginStorage
{
    public ComponentStorage(string componentName, string loopId)
    {
        GlobalDirectory = Path.Combine(Config.PathConfig.StoragePath, "PluginData", componentName);
        InstanceDirectory = Path.Combine(Config.PathConfig.StoragePath, "PluginData", componentName, loopId);
        Directory.CreateDirectory(GlobalDirectory);
        Directory.CreateDirectory(InstanceDirectory);
    }

    public string GlobalDirectory { get; }
    public string InstanceDirectory { get; }
}
```

- [ ] **Step 2: 添加缺失的 using（GetCustomAttribute 需要 System.Reflection）**

在文件顶部确保有：
```csharp
using System.Reflection;
using AgentLilara.PluginSDK;
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/Component/ComponentHost.cs
git commit -m "feat: add ComponentHost for loop component lifecycle and tool collection"
```

---

## Task 10: 主程序 — GlobalComponentHost（全局组件管理）

**Files:**
- Create: `AgentCoreProcessor/Component/GlobalComponentHost.cs`

- [ ] **Step 1: 创建 GlobalComponentHost.cs**

```csharp
// AgentCoreProcessor/Component/GlobalComponentHost.cs
using System.Reflection;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

/// <summary>
/// 管理所有 Global Component 实例。MasterEngine 持有唯一实例。
/// </summary>
internal class GlobalComponentHost
{
    private readonly ComponentEventBus _eventBus;
    private readonly IServiceProvider _services;
    private readonly Action<string> _wakeLoop;
    private readonly ComponentConfig _config;

    private readonly List<GlobalComponentInstance> _components = new();

    public GlobalComponentHost(
        ComponentEventBus eventBus,
        IServiceProvider services,
        Action<string> wakeLoop)
    {
        _eventBus = eventBus;
        _services = services;
        _wakeLoop = wakeLoop;
        _config = ComponentConfig.Load();
    }

    public async Task InitAsync()
    {
        var registrations = ComponentRegistry.GetGlobals();

        foreach (var reg in registrations)
        {
            try
            {
                var instance = CreateInstance(reg);
                if (instance == null) continue;
                _components.Add(instance);
                await instance.Component.OnInitAsync(instance.Context, InitReason.Fresh);
            }
            catch (Exception ex)
            {
                Util.FrameworkLogger.Log($"[GlobalComponentHost] Init failed for {reg.Type.Name}: {ex.Message}");
            }
        }

        Util.FrameworkLogger.Log($"[GlobalComponentHost] Initialized {_components.Count} global components");
    }

    public IEnumerable<ITool> GetVisibleTools(string loopType)
    {
        foreach (var inst in _components)
        {
            if (!ShouldShowTools(inst)) continue;
            foreach (var tool in inst.Component.Tools)
                yield return tool;
        }
    }

    public List<string> BuildPromptSections(LoopInfo caller)
    {
        var sections = new List<(int priority, string content)>();

        foreach (var inst in _components)
        {
            if (!inst.Context.IsEnabled) continue;
            var section = inst.Component.BuildPromptSection(caller);
            if (section != null)
                sections.Add((inst.Component.Meta.PromptPriority, section));
        }

        return sections
            .OrderBy(s => s.priority)
            .Select(s => s.content)
            .ToList();
    }

    public async Task EnableComponentAsync(string name)
    {
        var inst = _components.FirstOrDefault(i => i.Component.Meta.Name == name);
        if (inst == null || inst.Context.IsEnabled) return;
        inst.Context.SetEnabledDirect(true);
        try { await inst.Component.OnEnabledAsync(); }
        catch (Exception ex)
        {
            Util.FrameworkLogger.Log($"[GlobalComponentHost] OnEnabled error in {name}: {ex.Message}");
        }
    }

    public async Task DisableComponentAsync(string name)
    {
        var inst = _components.FirstOrDefault(i => i.Component.Meta.Name == name);
        if (inst == null || !inst.Context.IsEnabled) return;
        inst.Context.SetEnabledDirect(false);
        try { await inst.Component.OnDisabledAsync(); }
        catch (Exception ex)
        {
            Util.FrameworkLogger.Log($"[GlobalComponentHost] OnDisabled error in {name}: {ex.Message}");
        }
    }

    public async Task ShutdownAsync(ShutdownReason reason)
    {
        var timeout = _config.ShutdownTimeoutMs;
        using var cts = new CancellationTokenSource(timeout);

        var tasks = _components.Select(async inst =>
        {
            try { return await inst.Component.OnShutdownRequestedAsync(reason); }
            catch { return ShutdownResponse.Ok; }
        });

        try { await Task.WhenAll(tasks).WaitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            Util.FrameworkLogger.Log("[GlobalComponentHost] Shutdown timeout, forcing phase 2");
        }

        foreach (var inst in _components)
        {
            try { await inst.Component.OnShutdownAsync(reason).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex)
            {
                Util.FrameworkLogger.Log($"[GlobalComponentHost] OnShutdown error in {inst.Component.Meta.Name}: {ex.Message}");
            }
        }

        _components.Clear();
    }

    public IReadOnlyList<GlobalComponentInstance> Instances => _components;

    private GlobalComponentInstance? CreateInstance(ComponentRegistration reg)
    {
        var component = (IGlobalComponent?)Activator.CreateInstance(reg.Type);
        if (component == null) return null;

        var defaultEnabled = _config.IsEnabled(component.Meta.Name, component.Meta.DefaultEnabled);
        var storage = new ComponentStorage(component.Meta.Name, "_global");

        var context = new GlobalComponentContext(
            _eventBus, _services, storage, _wakeLoop,
            enabled =>
            {
                if (enabled) _ = EnableComponentAsync(component.Meta.Name);
                else _ = DisableComponentAsync(component.Meta.Name);
            },
            defaultEnabled);

        return new GlobalComponentInstance(component, context, reg);
    }

    private bool ShouldShowTools(GlobalComponentInstance inst)
    {
        var visibility = _config.GetVisibility(
            inst.Component.Meta.Name,
            inst.Registration.Type.GetCustomAttribute<ToolVisibilityAttribute>()?.Default ?? Visibility.FollowState);

        return visibility switch
        {
            Visibility.AlwaysVisible => true,
            Visibility.AlwaysHidden => false,
            Visibility.FollowState => inst.Context.IsEnabled,
            _ => inst.Context.IsEnabled
        };
    }
}

internal record GlobalComponentInstance(
    IGlobalComponent Component,
    GlobalComponentContext Context,
    ComponentRegistration Registration);
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Component/GlobalComponentHost.cs
git commit -m "feat: add GlobalComponentHost for global component lifecycle management"
```

---

## Task 11: PluginLoader 扩展 — 扫描 Component 类型

**Files:**
- Modify: `AgentCoreProcessor/Tool/Host/PluginLoader.cs`

- [ ] **Step 1: 修改 PluginLoader.cs — 增加 Component 扫描**

在 `LoadPlugin` 方法中，除了发现 ITool 类型外，同时发现带 `[Component]` 标记的类型并注册到 ComponentRegistry：

```csharp
// 在 PluginLoader 类中新增方法
private static List<Type> DiscoverComponentTypes(Assembly assembly)
{
    return assembly.GetExportedTypes()
        .Where(t => t.IsClass && !t.IsAbstract
            && t.GetCustomAttribute<ComponentAttribute>() != null)
        .ToList();
}
```

修改 `LoadPlugin` 方法，在 `DiscoverToolTypes` 之后增加：

```csharp
// 在 toolTypes 发现之后
var componentTypes = DiscoverComponentTypes(assembly);
foreach (var type in componentTypes)
{
    if (ComponentRegistry.Register(type))
    {
        var attr = type.GetCustomAttribute<ComponentAttribute>()!;
        FrameworkLogger.Log("PluginLoader", $"已注册组件: {attr.Name} ({attr.Scope}) 来自 {fileName}");
    }
}
```

修改卸载逻辑，在 `UnloadAll` 中增加：

```csharp
public void UnloadAll()
{
    foreach (var entry in loadedPlugins)
    {
        foreach (var name in entry.ToolNames)
            ToolRegistry.Unregister(name);
        foreach (var name in entry.ComponentNames)
            ComponentRegistry.Unregister(name);

        entry.LoadContext.Unload();
    }
    loadedPlugins.Clear();
}
```

在 `PluginEntry` 中增加字段：

```csharp
internal class PluginEntry
{
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public AssemblyLoadContext LoadContext { get; set; } = null!;
    public List<string> ToolNames { get; set; } = new();
    public List<string> ComponentNames { get; set; } = new();
}
```

在 `PluginLoadContext.Load` 中增加 SDK 程序集过滤：

```csharp
protected override Assembly? Load(AssemblyName assemblyName)
{
    if (assemblyName.Name == "AgentCoreProcessor" || assemblyName.Name == "AgentLilara.PluginSDK")
        return null;

    var path = resolver.ResolveAssemblyToPath(assemblyName);
    return path != null ? LoadFromAssemblyPath(path) : null;
}
```

- [ ] **Step 2: 增加 using 引用**

在 PluginLoader.cs 顶部增加：
```csharp
using System.Reflection;
using AgentCoreProcessor.Component;
```

- [ ] **Step 3: 编译验证**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/Tool/Host/PluginLoader.cs
git commit -m "feat: extend PluginLoader to scan and register Component types"
```

---

## Task 12: 插件迁移 — Plugin.WorkingTools → ILoopComponent

**Files:**
- Create: `Plugins/Plugin.WorkingTools/WorkingToolsComponent.cs`
- Modify: `Plugins/Plugin.WorkingTools/ThinkingNotesTool.cs` (移除 IPromptContributor 如有)
- Modify: `Plugins/Plugin.WorkingTools/PinboardTool.cs`
- Modify: `Plugins/Plugin.WorkingTools/RetainListTool.cs`

- [ ] **Step 1: 创建 WorkingToolsComponent.cs**

```csharp
// Plugins/Plugin.WorkingTools/WorkingToolsComponent.cs
using AgentLilara.PluginSDK;

namespace Plugin.WorkingTools;

[Component(Name = "working-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Disabled)]
[ToolVisibility(Default = Visibility.AlwaysVisible)]
public class WorkingToolsComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private ThinkingNotesTool? _thinkingNotes;
    private PinboardTool? _pinboard;
    private RetainListTool? _retainList;

    public override ComponentMeta Meta => new()
    {
        Name = "working-tools",
        Description = "思考笔记、便签板、缓存列表",
        DefaultEnabled = true,
        PromptPriority = 45
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_thinkingNotes != null) yield return _thinkingNotes;
            if (_pinboard != null) yield return _pinboard;
            if (_retainList != null) yield return _retainList;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;
        _thinkingNotes = new ThinkingNotesTool(context.Storage, context.LoopId);
        _pinboard = new PinboardTool(context.Storage);
        _retainList = new RetainListTool(context.Storage);
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        var sections = new List<string>();

        var notes = _thinkingNotes?.BuildSection();
        if (notes != null) sections.Add(notes);

        var board = _pinboard?.BuildSection();
        if (board != null) sections.Add(board);

        var retain = _retainList?.BuildSection();
        if (retain != null) sections.Add(retain);

        return sections.Count > 0 ? string.Join("\n", sections) : null;
    }
}
```

- [ ] **Step 2: 修改 ThinkingNotesTool — 增加 BuildSection 方法**

在 ThinkingNotesTool 中增加一个构造函数重载接受 `IPluginStorage` + `loopId`，以及 `BuildSection()` 方法：

```csharp
// 新增构造函数（Component 模式使用）
public ThinkingNotesTool(IPluginStorage storage, string loopId)
{
    // 使用 storage.InstanceDirectory 作为 notebook 路径
    this.notebookDir = Path.Combine(storage.InstanceDirectory, "notebooks");
    this.defaultNotebook = loopId;
    Directory.CreateDirectory(notebookDir);
}

public string? BuildSection()
{
    var path = Path.Combine(notebookDir, $"{SanitizeFileName(defaultNotebook)}.txt");
    if (!File.Exists(path)) return null;
    var content = File.ReadAllText(path);
    if (string.IsNullOrWhiteSpace(content)) return null;
    return $"你的思考笔记（notebook={defaultNotebook}）：\n{content}";
}
```

- [ ] **Step 3: 修改 PinboardTool — 增加 BuildSection 方法**

```csharp
public PinboardTool(IPluginStorage storage)
{
    this.filePath = Path.Combine(storage.GlobalDirectory, "pinboard.json");
}

public string? BuildSection()
{
    var board = LoadBoard();
    if (board.Count == 0) return null;
    var sb = new StringBuilder("[便签板]\n");
    foreach (var (label, content) in board)
        sb.AppendLine($"- {label}: {content}");
    return sb.ToString();
}
```

- [ ] **Step 4: 修改 RetainListTool — 增加 BuildSection 方法**

```csharp
public RetainListTool(IPluginStorage storage)
{
    this.filePath = Path.Combine(storage.GlobalDirectory, "retain", "items.json");
    Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
}

public string? BuildSection()
{
    var items = LoadItems();
    if (items.Count == 0) return null;
    var sb = new StringBuilder("[缓存列表]\n");
    foreach (var (label, content) in items)
    {
        var preview = content.Length > 120 ? content[..120] + "..." : content;
        sb.AppendLine($"- [{label}] {preview}");
    }
    return sb.ToString();
}
```

- [ ] **Step 5: 确保 Plugin.WorkingTools.csproj 引用 SDK**

```xml
<ItemGroup>
  <ProjectReference Include="..\..\AgentLilara.PluginSDK\AgentLilara.PluginSDK.csproj" />
</ItemGroup>
```

- [ ] **Step 6: 编译验证**

Run: `dotnet build Plugins/Plugin.WorkingTools/Plugin.WorkingTools.csproj`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add Plugins/Plugin.WorkingTools/
git commit -m "feat: migrate Plugin.WorkingTools to ILoopComponent with prompt injection"
```

---

## Task 13: 插件迁移 — Plugin.BasicTools → IGlobalComponent

**Files:**
- Create: `Plugins/Plugin.BasicTools/BasicToolsComponent.cs`

- [ ] **Step 1: 创建 BasicToolsComponent.cs**

```csharp
// Plugins/Plugin.BasicTools/BasicToolsComponent.cs
using AgentLilara.PluginSDK;

namespace Plugin.BasicTools;

[Component(Name = "basic-tools", Scope = ComponentScope.Global)]
[ToolVisibility(Default = Visibility.AlwaysVisible)]
public class BasicToolsComponent : GlobalComponentBase
{
    private SpeakTool? _speak;
    private SendMediaTool? _sendMedia;

    public override ComponentMeta Meta => new()
    {
        Name = "basic-tools",
        Description = "基础通信工具（speak, send_media）",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_speak != null) yield return _speak;
            if (_sendMedia != null) yield return _sendMedia;
        }
    }

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _speak = new SpeakTool();
        _sendMedia = new SendMediaTool();
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build Plugins/Plugin.BasicTools/Plugin.BasicTools.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.BasicTools/BasicToolsComponent.cs
git commit -m "feat: migrate Plugin.BasicTools to IGlobalComponent"
```

---

## Task 14: 插件迁移 — Plugin.MemoryTools → IGlobalComponent

**Files:**
- Create: `Plugins/Plugin.MemoryTools/MemoryToolsComponent.cs`

- [ ] **Step 1: 创建 MemoryToolsComponent.cs**

```csharp
// Plugins/Plugin.MemoryTools/MemoryToolsComponent.cs
using AgentLilara.PluginSDK;

namespace Plugin.MemoryTools;

[Component(Name = "memory-tools", Scope = ComponentScope.Global)]
[ToolVisibility(Default = Visibility.AlwaysVisible)]
public class MemoryToolsComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private MemoryTool? _memoryTool;

    public override ComponentMeta Meta => new()
    {
        Name = "memory-tools",
        Description = "记忆存储与检索",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_memoryTool != null) yield return _memoryTool;
        }
    }

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;
        var memoryAccess = context.GetService<IMemoryAccess>();
        if (memoryAccess != null)
            _memoryTool = new MemoryTool(memoryAccess);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build Plugins/Plugin.MemoryTools/Plugin.MemoryTools.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.MemoryTools/MemoryToolsComponent.cs
git commit -m "feat: migrate Plugin.MemoryTools to IGlobalComponent"
```

---

## Task 15: 插件迁移 — Plugin.FileTools → IGlobalComponent

**Files:**
- Create: `Plugins/Plugin.FileTools/FileToolsComponent.cs`

- [ ] **Step 1: 创建 FileToolsComponent.cs**

```csharp
// Plugins/Plugin.FileTools/FileToolsComponent.cs
using AgentLilara.PluginSDK;

namespace Plugin.FileTools;

[Component(Name = "file-tools", Scope = ComponentScope.Global)]
[ToolVisibility(Default = Visibility.FollowState)]
public class FileToolsComponent : GlobalComponentBase
{
    private readonly List<ITool> _tools = new();

    public override ComponentMeta Meta => new()
    {
        Name = "file-tools",
        Description = "文件读写操作",
        DefaultEnabled = true,
        PromptPriority = 100
    };

    public override IEnumerable<ITool> Tools => _tools;

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _tools.Add(new ReadTextTool());
        _tools.Add(new WriteTextTool());
        _tools.Add(new ListDirTool());
        _tools.Add(new MoveFileTool());
        _tools.Add(new DeleteFileTool());
        _tools.Add(new CopyFileTool());
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build Plugins/Plugin.FileTools/Plugin.FileTools.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Plugins/Plugin.FileTools/FileToolsComponent.cs
git commit -m "feat: migrate Plugin.FileTools to IGlobalComponent"
```

---

## Task 16: 工具列表分组展示 — ToolListFormatter

**Files:**
- Create: `AgentCoreProcessor/Component/ToolListFormatter.cs`

- [ ] **Step 1: 创建 ToolListFormatter.cs**

工具列表按组件分组展示给模型。启用的组件只列工具名（描述由 API 原生 tool schema 承载），禁用的组件一行摘要。禁用组件的工具不传给 API。

```csharp
// AgentCoreProcessor/Component/ToolListFormatter.cs
using System.Text;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Component;

internal record ToolGroup(
    string ComponentName,
    string Description,
    ComponentScope Scope,
    bool IsEnabled,
    IReadOnlyList<ITool> Tools);

internal static class ToolListFormatter
{
    public static List<ToolGroup> CollectGroups(
        ComponentHost? loopHost,
        GlobalComponentHost? globalHost)
    {
        var groups = new List<ToolGroup>();

        if (globalHost != null)
        {
            foreach (var inst in globalHost.Instances)
            {
                groups.Add(new ToolGroup(
                    inst.Component.Meta.Name,
                    inst.Component.Meta.Description,
                    ComponentScope.Global,
                    inst.Context.IsEnabled,
                    inst.Component.Tools.ToList()));
            }
        }

        if (loopHost != null)
        {
            foreach (var inst in loopHost.Instances)
            {
                groups.Add(new ToolGroup(
                    inst.Component.Meta.Name,
                    inst.Component.Meta.Description,
                    ComponentScope.Loop,
                    inst.Context.IsEnabled,
                    inst.Component.Tools.ToList()));
            }
        }

        return groups;
    }

    /// <summary>
    /// 构建组件目录注入 prompt。启用的列工具名，禁用的也列工具名但标注状态。
    /// 工具描述不重复（由 API tool schema 提供）。
    /// </summary>
    public static string? BuildToolOverviewSection(List<ToolGroup> groups)
    {
        if (groups.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("[组件目录]");

        foreach (var g in groups)
        {
            var scope = g.Scope == ComponentScope.Global ? "全局" : "循环";
            var toolNames = string.Join(", ", g.Tools.Select(t => t.Name));

            if (g.IsEnabled)
            {
                sb.AppendLine($"▸ {g.ComponentName}（{scope}）: {toolNames}");
            }
            else
            {
                sb.AppendLine($"▹ {g.ComponentName}（{scope} · 已禁用）: {toolNames} [enable_component(\"{g.ComponentName}\") 启用]");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 收集传给 API 的工具定义（仅启用组件的工具）。
    /// </summary>
    public static List<ITool> CollectVisibleTools(List<ToolGroup> groups)
    {
        var tools = new List<ITool>();
        foreach (var g in groups)
        {
            if (!g.IsEnabled) continue;
            tools.AddRange(g.Tools);
        }
        return tools;
    }
}
```

- [ ] **Step 2: 编译验证**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Component/ToolListFormatter.cs
git commit -m "feat: add ToolListFormatter for grouped tool display to model"
```

---

## Task 17: 引擎集成 — ChannelEngine 使用 ComponentHost

**Files:**
- Modify: `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs`

- [ ] **Step 1: 在 ChannelEngine 中添加 ComponentHost 字段**

```csharp
private ComponentHost? componentHost;
```

- [ ] **Step 2: 在初始化方法中创建并初始化 ComponentHost**

在 modules 初始化之后（或替代部分 modules），创建 ComponentHost：

```csharp
componentHost = new ComponentHost(
    channelId, "channel", ctx.ComponentEventBus, ctx.Services,
    () => gate.Signal());
await componentHost.InitAsync();
```

- [ ] **Step 3: 使用 ToolListFormatter 收集工具**

替代原有的扁平工具列表收集，使用分组方式：

```csharp
var groups = ToolListFormatter.CollectGroups(componentHost, ctx.GlobalComponentHost);
var visibleTools = ToolListFormatter.CollectVisibleTools(groups);
// visibleTools 合并到传给模型的工具列表中
```

- [ ] **Step 4: 在 Prompt 构建中注入组件概览 + Component prompt sections**

```csharp
// 组件概览（工具分组信息）
var toolOverview = ToolListFormatter.BuildToolOverviewSection(groups);
if (toolOverview != null) promptSections.Add(toolOverview);

// 各组件自己的 prompt 注入
var componentSections = componentHost?.BuildPromptSections() ?? new();
promptSections.AddRange(componentSections);

// Global component 的 prompt 注入
var globalSections = ctx.GlobalComponentHost?.BuildPromptSections(
    new LoopInfo(channelId, "channel")) ?? new();
promptSections.AddRange(globalSections);
```

- [ ] **Step 5: 在循环生命周期中调用 ComponentHost 钩子**

```csharp
// 循环唤醒时
await componentHost.OnActivatedAsync();

// 模型调用前
await componentHost.OnBeforeInvokeAsync();

// 模型调用后
await componentHost.OnAfterInvokeAsync();

// 循环暂停时
await componentHost.OnPauseAsync();
```

- [ ] **Step 6: 在引擎关闭时调用 ComponentHost.ShutdownAsync**

```csharp
if (componentHost != null)
    await componentHost.ShutdownAsync(ShutdownReason.Destroy);
```

- [ ] **Step 7: 移除被 Component 替代的 EngineModule**

从 modules 列表中移除：
- `ThinkingNotesModule`（已由 WorkingToolsComponent 替代）
- `PinboardModule`（同上）
- `RetainListModule`（同上）

保留不迁移的模块（DelegationModule、SystemNotificationModule、LoopControlModule 等暂不动）。

- [ ] **Step 8: 编译验证**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded

- [ ] **Step 9: Commit**

```bash
git add AgentCoreProcessor/Engine/Worker/ChannelEngine.cs
git commit -m "feat: integrate ComponentHost into ChannelEngine with grouped tool display"
```

---

## Task 18: 引擎集成 — SystemEngine 使用 ComponentHost

**Files:**
- Modify: `AgentCoreProcessor/Engine/System/SystemEngine.cs`

- [ ] **Step 1: 在 SystemEngine 中添加 ComponentHost 字段**

```csharp
private ComponentHost? componentHost;
```

- [ ] **Step 2: 在初始化中创建 ComponentHost（loopType = "system"）**

```csharp
componentHost = new ComponentHost(
    "system", "system", ctx.ComponentEventBus, ctx.Services,
    () => gate.Signal());
await componentHost.InitAsync();
```

- [ ] **Step 3: 合并工具和 prompt sections（同 ChannelEngine 模式）**

- [ ] **Step 4: 在生命周期中调用钩子**

- [ ] **Step 5: 关闭时调用 ShutdownAsync**

- [ ] **Step 6: 编译验证**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add AgentCoreProcessor/Engine/System/SystemEngine.cs
git commit -m "feat: integrate ComponentHost into SystemEngine"
```

---

## Task 19: MasterEngine 集成 — GlobalComponentHost + ComponentEventBus

**Files:**
- Modify: `AgentCoreProcessor/Engine/Core/MasterEngine.cs`

- [ ] **Step 1: 在 MasterEngine 中添加字段**

```csharp
private ComponentEventBus componentEventBus = new();
private GlobalComponentHost? globalComponentHost;
```

- [ ] **Step 2: 在 InitAsync 中初始化 GlobalComponentHost**

在 PluginLoader.LoadAll() 之后（此时 ComponentRegistry 已填充）：

```csharp
globalComponentHost = new GlobalComponentHost(
    componentEventBus, services,
    loopId => WakeLoop(loopId));
await globalComponentHost.InitAsync();
```

- [ ] **Step 3: 暴露 ComponentEventBus 给子引擎**

通过 EngineContext 或直接传递，让 ChannelEngine/SystemEngine 能拿到 componentEventBus：

```csharp
// 在 EngineContext 中增加
public ComponentEventBus ComponentEventBus { get; init; }
```

- [ ] **Step 4: 暴露 GlobalComponentHost 的工具给循环**

循环在收集工具时，除了自己的 ComponentHost 工具，还要合并 GlobalComponentHost 的工具：

```csharp
// 在 ChannelEngine/SystemEngine 的工具收集中
var globalTools = ctx.GlobalComponentHost?.GetVisibleTools("channel") ?? Enumerable.Empty<ITool>();
```

- [ ] **Step 5: 在 MasterEngine 关闭时调用 GlobalComponentHost.ShutdownAsync**

```csharp
if (globalComponentHost != null)
    await globalComponentHost.ShutdownAsync(ShutdownReason.Destroy);
```

- [ ] **Step 6: 编译验证**

Run: `dotnet build AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add AgentCoreProcessor/Engine/Core/MasterEngine.cs
git commit -m "feat: integrate GlobalComponentHost and ComponentEventBus into MasterEngine"
```

---

## Task 20: 全量编译 + 运行验证

- [ ] **Step 1: 全 solution 编译**

Run: `dotnet build AgentLilaraProjectSolution.sln`
Expected: Build succeeded, 0 errors

- [ ] **Step 2: 运行验证**

```bash
taskkill /IM AgentCoreProcessor.exe /F 2>/dev/null
dotnet run --project AgentCoreProcessor/AgentCoreProcessor.csproj
```

Expected:
- 日志显示 "插件加载完成" 包含 Component 注册信息
- 日志显示 "GlobalComponentHost Initialized N global components"
- 频道循环启动时日志显示 ComponentHost 初始化
- speak/send_media 等工具正常可用

- [ ] **Step 3: --test 模式试运行**

```bash
dotnet run --project AgentCoreProcessor/AgentCoreProcessor.csproj -- --test
```

验证：
- 工具列表包含所有 Component 提供的工具
- WorkingTools 的 prompt 注入正常工作（思考笔记/便签板/缓存列表）
- 基础通信正常

- [ ] **Step 4: 最终 Commit**

```bash
git add -A
git commit -m "feat: Component system foundation complete - SDK + Host + Plugin migration"
```
