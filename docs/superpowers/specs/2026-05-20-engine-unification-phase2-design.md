# 引擎循环统一 Phase 2 设计规范

> 状态：定稿。Phase 1 已完成（9 commits），本文覆盖 Phase 2 核心范围。

## 范围

| 包含 | 暂缓 |
|------|------|
| IInjectProvider + InjectContext + IEngineLifecycle 接口 | 委托板插件化（DelegationModule → DelegationPlugin） |
| ChannelSignal 类型化（4个 record） | MemoryWindowModule 迁移 |
| ModuleBus 每引擎独立总线 | WebUI Phase 3 旧页面迁移 |
| PluginLoader 多类型发现 + 构造注入 | |
| Engine 双来源收集（内部模块 + 插件） | |
| B/C 类模块清理（6 个文件删除） | |

---

## 一、核心接口

### 1.1 IInjectProvider

```csharp
/// <summary>注入上下文。框架在调用注入钩子时传入，插件按需读取。</summary>
class InjectContext
{
    public string Mode { get; init; }         // "express" | "working" | "system"
    public int CurrentRound { get; init; }     // Start 时为 0，Round 从 1 开始
    public int MaxRounds { get; init; }
    public int EstimatedTokens { get; init; }
}

/// <summary>插件/模块注入接口。框架在正确时机调用，注入什么由插件自行决定。</summary>
interface IInjectProvider
{
    int InjectPriority { get; }               // 越小越靠前，默认 50

    /// <summary>循环开始时调一次。稳定快照类内容（记忆、便签板等）。无内容返回 null。</summary>
    Task<string?> BuildStartInjectAsync(InjectContext ctx);

    /// <summary>每轮调一次。实时变化类内容（消息、通知、委托状态、压缩提醒等）。无内容返回 null。</summary>
    Task<string?> BuildRoundInjectAsync(InjectContext ctx);
}
```

**设计原则**：框架只提供时机钩子 + 上下文信息。插件自己决定是否注入、注入什么。BuildRoundInjectAsync 中走空炮（返回 null）是正常行为——比如便签板在 Start 注入一次后，Round 阶段多数轮次返回 null。

### 1.2 IEngineLifecycle

```csharp
interface IEngineLifecycle
{
    /// <summary>引擎启动时调一次。替代 EngineModule.Attach。</summary>
    Task OnInitializeAsync(IServiceProvider services);

    /// <summary>引擎停止时调一次。替代 EngineModule.Reset。</summary>
    Task OnShutdownAsync();
}
```

EventBus / ModuleBus / Gate / IMemoryAccess 等通过 services 解析或构造注入获取，不在接口声明。

### 1.3 EngineModule 基类改造

```csharp
abstract class EngineModule : IInjectProvider, IEngineLifecycle
{
    public abstract string Name { get; }
    public virtual int InjectPriority => 50;

    // IInjectProvider — 默认映射旧方法
    public virtual Task<string?> BuildStartInjectAsync(InjectContext ctx)
        => Task.FromResult(BuildPromptSection(/* 由 ctx.Mode 映射 */));
    public virtual Task<string?> BuildRoundInjectAsync(InjectContext ctx)
        => Task.FromResult<string?>(null);

    // IEngineLifecycle
    public virtual Task OnInitializeAsync(IServiceProvider services)
        => Task.CompletedTask;
    public virtual Task OnShutdownAsync()
        { Reset(); return Task.CompletedTask; }

    // ---- 逐步废弃 ----
    [Obsolete] public virtual void Attach(ILoopBus bus) { }
    [Obsolete] public virtual string? BuildPromptSection(EngineMode mode) => null;
    [Obsolete] public virtual IEnumerable<ITool> GetTools(EngineMode mode) => [];
    [Obsolete] public virtual void Reset() { }
}
```

旧模块编译通过，新插件的 `BuildStartInjectAsync` / `BuildRoundInjectAsync` 直接 override 新方法。

---

## 二、ChannelSignal 类型化

```csharp
abstract record ChannelSignal;

/// <summary>新消息到达（用户发言、系统推送等）。</summary>
record NewMessageSignal(IncomingMessage Message, SessionContext Session) : ChannelSignal;

/// <summary>EventBus 事件到达（委托完成、系统通知、引擎间通信）。</summary>
record BusEventSignal(EngineEvent Event) : ChannelSignal;

/// <summary>压缩完成。包含新摘要 + 保留的对话历史。</summary>
record CompressionSignal(string Summary, List<Message> RetainedHistory) : ChannelSignal;

/// <summary>模式切换（Express ↔ Working）。</summary>
record ModeSwitchSignal(string NewMode, string? Reason) : ChannelSignal;
```

**引擎内部流**：

```
外部事件 → ConcurrentQueue<ChannelSignal> _signalBuffer
BuildRoundInjectAsync 时 drain:
  NewMessageSignal  → 格式化为 <新消息>name: content</新消息>
  BusEventSignal    → 格式化为 [系统事件]...
  CompressionSignal → 下轮重建 Agent，摘要进 history
  ModeSwitchSignal  → 更新 InjectContext.Mode
```

- ChannelSignal 不对外暴露给 IInjectProvider
- 插件通过 `InjectContext.Mode` 感知模式变化
- 当前 `activeBatch`/`interceptorInjections`/`escalationReason` 三个字段 → 一个 Queue 替代
- ChannelSignal 是运行时内存对象，不进 Signal DB

---

## 三、ModuleBus

```csharp
/// <summary>每引擎独立的模块间通信总线。</summary>
class ModuleBus
{
    IDisposable Subscribe<T>(Action<T> handler) where T : class;
    void Publish<T>(T message) where T : class;
}
```

| 属性 | 说明 |
|------|------|
| 生命周期 | 每个引擎一个实例 |
| 与 EventBus 关系 | EventBus 跨引擎全局事件；ModuleBus 引擎内模块间事件 |
| 获取方式 | 构造注入或 IServiceProvider 解析 |
| 典型消息 | ToolExecutedEvent、LoopStateChangedEvent |
| 替代 | 统一替代 `ILoopBus`（EngineModule.Attach 用）和 `ComponentEventBus`（Component 用） |

`EngineModule.Attach(ILoopBus)` 移除，改为 `IEngineLifecycle.OnInitializeAsync(services)` 中自行解析 `ModuleBus` 并订阅。
`ComponentEventBus` 移除，Component 通过构造注入直接拿 `ModuleBus`。

---

## 四、Component 生态位

Component 是 **IInjectProvider 的超集**——它不只是注入文本，还捆绑工具、管理生命周期、拥有独立存储。

```
层级关系：

  ITool           ← 全局工具执行，无注入，无生命周期
  IInjectProvider ← 轻量文本注入
  IEngineLifecycle ← 极简生命周期（start/shutdown）
  Component       ← 重量注入器 = IInjectProvider + 工具捆绑
                      + 完整生命周期（Init/Activate/BeforeAfterInvoke/Pause/Shutdown）
                      + 独立存储 + enable/disable
```

### 4.1 统一变更

| 旧 | 新 |
|----|----|
| `Component.BuildPromptSection()` | 废弃。Component 直接实现 `IInjectProvider.BuildStartInjectAsync/RoundInjectAsync` |
| `ComponentEventBus`（每 ComponentHost 一个） | 合并进 `ModuleBus` |
| 其余生命周期 | 保留（`OnInitAsync`/`OnActivatedAsync`/`OnBeforeInvokeAsync` 等） |

### 4.2 Component 构造注入

Component 通过 PluginLoader 发现（`[Component]` attribute），`ComponentHost.CreateInstance` 改为构造注入实例化：

```csharp
class PinboardComponent : ILoopComponent, IInjectProvider
{
    // 构造注入
    public PinboardComponent(ModuleBus bus, IMemoryAccess memory)
    {
        bus.Subscribe<ToolExecutedEvent>(OnToolExecuted);
        // ...
    }

    // ILoopComponent
    public ComponentMeta Meta => new("pinboard", ...);
    public IReadOnlyList<ITool> Tools => ...;

    // IInjectProvider
    public int InjectPriority => 55;
    public Task<string?> BuildStartInjectAsync(InjectContext ctx) => ...;
    public Task<string?> BuildRoundInjectAsync(InjectContext ctx) => ...;
}
```

### 4.3 Component 获取 ModuleBus 的两条路

1. **构造注入**（首选）：`new PinboardComponent(ModuleBus bus, ...)`
2. **IServiceProvider 兜底**：`IEngineLifecycle.OnInitializeAsync(services)` → `services.GetService<ModuleBus>()`

---

## 五、PluginLoader 构造注入 + 多类型发现

### 5.1 可注入类型

插件构造函数可声明以下类型，PluginLoader / ComponentHost 反射扫描自动匹配注入：

```
EventBus        → 全局事件总线
ModuleBus       → 引擎内模块总线（替代 ComponentEventBus）
Gate            → 循环闸门（需要强制唤醒时声明）
IMemoryAccess   → 记忆读写（已有）
IServiceProvider → 完整容器（兜底）
```

`ITool` / `IInjectProvider` / `IEngineLifecycle` / `Component` 均享受同样的构造注入能力。

### 5.2 多类型发现

同一类可同时实现多个接口：

```csharp
class PinboardTool : ITool, IInjectProvider
{
    // ITool
    public string Name => "pinboard";
    public async Task<ToolResult> ExecuteAsync(...) { /* 写便签 */ }

    // IInjectProvider
    public int InjectPriority => 55;
    public async Task<string?> BuildStartInjectAsync(InjectContext ctx)
        => "[便签板]\n- todo: 修bug";
    public Task<string?> BuildRoundInjectAsync(InjectContext ctx)
        => Task.FromResult<string?>(null);
}
```

### 5.3 发现流程

```
扫描 Plugins/*.dll
  ├─ ITool            → ToolRegistry.Register(tool)
  ├─ IInjectProvider  → 记录 Type，引擎按需实例化（新）
  ├─ IWebUIProvider   → ProviderRegistry.Register
  ├─ IEngineLifecycle → 记录 Type，引擎按需实例化（新）
  └─ Component        → ComponentRegistry.Register
                           ↓ ComponentHost.InitAsync 时实例化
                           构造注入 ModuleBus/EventBus 等
                           同时实现 IInjectProvider 则注入文本
                           同时实现 IEngineLifecycle 则管理生命周期
```

Component 的构造注入由 `ComponentHost.CreateInstance` 负责，PluginLoader 只记录 Type。

### 5.4 延迟实例化

插件 IInjectProvider 依赖引擎级实例（Gate、ModuleBus），不能全局单例。PluginLoader 只记录 Type，引擎自己实例化：

```csharp
class PluginLoader
{
    IReadOnlyList<Type> InjectProviderTypes { get; }
    IReadOnlyList<Type> LifecycleTypes { get; }

    IInjectProvider? InstantiateInjectProvider(Type type, IServiceProvider engineServices);
    IEngineLifecycle? InstantiateLifecycle(Type type, IServiceProvider engineServices);
}
```

### 5.5 PluginEntry 扩展

```csharp
class PluginEntry
{
    // ... 现有字段
    List<string> InjectProviderNames { get; set; }  // WebUI 可查询
    List<string> LifecycleNames { get; set; }
}
```

---

## 六、引擎双来源收集

```csharp
class ChannelEngine
{
    List<IInjectProvider> _injectProviders = new();

    async Task InitializeInjectProviders()
    {
        // 1. 内部模块（EngineModule : IInjectProvider）
        _injectProviders.AddRange(modules);

        // 2. 插件 IInjectProvider（延迟实例化）
        foreach (var type in pluginLoader.InjectProviderTypes)
        {
            var provider = pluginLoader.InstantiateInjectProvider(type, engineServices);
            if (provider != null) _injectProviders.Add(provider);
        }
    }

    // BuildStartInjectAsync / BuildRoundInjectAsync 中统一收集
}
```

---

## 七、注入桶划分

### BuildStartInjectAsync（循环级，一次）—"知道有这么个东西就行"

| 来源 | 内容 |
|------|------|
| 记忆窗口 | 相关记忆摘要 |
| 便签板 | 便签摘要 |
| 缓存列表 | 保留项摘要 |
| 思考笔记 | 笔记摘要 |
| 任务列表 | 任务摘要 |
| 工具状态 | 禁用列表 |

### BuildRoundInjectAsync（轮次级，每轮）—"现在就来了 / 这个很重要"

| 来源 | 内容 |
|------|------|
| 新消息 drain | ChannelSignal → 格式化 |
| 系统通知 | 任务完成/委托结果 |
| 委托状态 | 进行中/排队/完成 |
| 压缩提示 | L1/L2 提醒 |
| 循环控制 | 轮次/模式提示 |
| 拦截器注入 | 系统拦截提示 |
| 升级原因 | escalate 触发 |

---

## 八、B/C 类清理

### B 类 — 工具双接口合并

| 删除的模块文件 | 合并到的工具（Plugin.WorkingTools） |
|------|------|
| `Engine/Worker/Modules/PinboardModule.cs` | `PinboardTool` → `: ITool, IInjectProvider` |
| `Engine/Worker/Modules/RetainListModule.cs` | `RetainListTool` → `: ITool, IInjectProvider` |
| `Engine/Worker/Modules/ThinkingNotesModule.cs` | `ThinkingNotesTool` → `: ITool, IInjectProvider` |
| `Engine/Worker/Modules/TaskListModule.cs` | `TaskListTool` → `: ITool, IInjectProvider` |

### C 类 — 直接删除

| 删除 | 替代 |
|------|------|
| `SpeakModule.cs` | `Plugin.BasicTools` speak 工具 |
| `SignalDispatchModule.cs` | `ModuleBus` 直接订阅 `ToolExecutedEvent` |

---

## 九、完整数据流

```
Gate 打开
  │
  └─ Agent.RunAsync
       │
       ├─ BuildStartInjectAsync（循环级，一次）
       │   按 InjectPriority 排序收集：
       │   ┌──────────────────────────────────────────┐
       │   │ [引擎] 记忆窗口快照                         │
       │   │ [插件] PinboardTool → 便签摘要              │
       │   │ [插件] RetainListTool → 保留项              │
       │   │ [插件] ThinkingNotesTool → 笔记摘要         │
       │   │ [插件] TaskListTool → 任务摘要              │
       │   │ [模块] ToolStatusModule → 禁用列表          │
       │   │ [模块] ContextCompressionModule → 摘要      │
       │   │ [模块] SystemStatusModule → 状态            │
       │   │ [Component] 各 Component 的 BuildStartInjectAsync │
       │   └──────────────────────────────────────────┘
       │   合并 → Message → 追加到 Agent.History
       │
       ├─ Round 1:
       │   ├─ BuildRoundInjectAsync（轮次级，每轮）
       │   │   ┌──────────────────────────────────────────┐
       │   │   │ [引擎] drain ConcurrentQueue<ChannelSignal>  │
       │   │   │   → NewMessageSignal → <新消息>             │
       │   │   │   → BusEventSignal → [系统事件]             │
       │   │   │   → CompressionSignal → 重建 Agent          │
       │   │   │   → ModeSwitchSignal → 更新 mode            │
       │   │   │ [模块] LoopControlModule → 轮次提示         │
       │   │   │ [模块] SystemNotificationModule → 通知      │
       │   │   │ [模块] PendingEventsModule → 待处理         │
       │   │   │ [模块] TaskQueueModule → 任务队列           │
       │   │   │ [压缩] CompressionTierModule → 压缩提醒     │
       │   │   └──────────────────────────────────────────┘
       │   ├─ 拼 messages → Core.InvokeAsync
       │   └─ 执行工具 → 工具可发 EventBus/ModuleBus
       │       → 同轮后续 IInjectProvider 可感知
       │
       ├─ Round 2: BuildRoundInjectAsync（同上）
       └─ ... → Stop
```

---

## 十、文件变更清单

| 操作 | 文件 |
|------|------|
| **新建** | `Engine/Core/IInjectProvider.cs`（IInjectProvider + InjectContext） |
| **新建** | `Engine/Core/IEngineLifecycle.cs` |
| **新建** | `Engine/Core/ModuleBus.cs` |
| **新建** | `Engine/Core/ChannelSignal.cs`（4个 record） |
| **修改** | `Engine/Worker/EngineModule.cs` → 实现 IInjectProvider + IEngineLifecycle |
| **修改** | `Engine/Worker/ChannelEngine.cs` → ChannelSignal 缓冲 + 双来源收集 |
| **修改** | `Engine/System/SystemEngine.cs` → 同上 |
| **修改** | `Tool/Host/PluginLoader.cs` → 多类型发现 + 构造注入 + 延迟实例化 |
| **修改** | `Component/ComponentHost.cs` → CreateInstance 改为构造注入，ComponentEventBus → ModuleBus |
| **修改** | `Plugins/Plugin.WorkingTools/` 4 个工具 → 加 IInjectProvider 实现 |
| **删除** | `Engine/Worker/Modules/PinboardModule.cs` |
| **删除** | `Engine/Worker/Modules/RetainListModule.cs` |
| **删除** | `Engine/Worker/Modules/ThinkingNotesModule.cs` |
| **删除** | `Engine/Worker/Modules/TaskListModule.cs` |
| **删除** | `Engine/Worker/Modules/SpeakModule.cs` |
| **删除** | `Engine/Worker/Modules/SignalDispatchModule.cs` |

---

## 十一、WebUI

- 不新增接口。IInjectProvider 只做注入逻辑
- 需要 UI 的插件额外实现已有的 `IWebUIProvider`，注册卡片/页面
- PluginLoader 的 `PluginEntry` 扩展字段供 `/config/plugins` 页展示注入器列表
