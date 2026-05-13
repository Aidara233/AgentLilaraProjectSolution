# Component 系统设计

## 概述

将当前的"工具 + 引擎模块"二元架构统一为 **Component** 模型。所有工具归属于 Component，Component 提供可选的生命周期钩子、事件订阅、Prompt 注入能力。

核心目标：
- 普通开发者只做工具（退化 Component，纯容器）
- 高级开发者做完整 Component（生命周期 + 事件 + 状态）
- 替代硬编码的 EngineModule，实现热替换
- 双循环协作（delegate_task 等）完全由插件实现

## Component 分类

### IGlobalComponent

全局单例，不绑定到任何循环实例。

适用场景：
- 纯工具容器（如 FileTools，所有循环都能调用）
- 跨循环协调（如定时任务调度器，能唤醒指定循环）

### ILoopComponent

Per-循环实例化，绑定到具体的循环实例（频道循环或系统循环）。

适用场景：
- 需要循环内状态的功能（如 DelegationComponent 跟踪本频道的委托）
- 需要生命周期钩子的功能（如在循环唤醒时注入 prompt）
- 组件管理器（管理当前循环的组件启用/禁用）

## 接口定义

### ILoopComponent

```csharp
public interface ILoopComponent
{
    ComponentMeta Meta { get; }
    IEnumerable<ITool> Tools { get; }

    Task OnInitAsync(ILoopComponentContext context);
    Task OnDestroyAsync();
    Task OnActivatedAsync();   // 循环从闲置被唤醒
    Task OnPauseAsync();       // 循环进入等待
    Task OnBeforeInvokeAsync(); // 模型调用前
    Task OnAfterInvokeAsync();  // 模型调用后

    string? BuildPromptSection();
}
```

### IGlobalComponent

```csharp
public interface IGlobalComponent
{
    ComponentMeta Meta { get; }
    IEnumerable<ITool> Tools { get; }

    Task OnInitAsync(IGlobalComponentContext context);
    Task OnDestroyAsync();

    string? BuildPromptSection(LoopInfo caller);
}
```

### ComponentMeta

```csharp
public class ComponentMeta
{
    public string Name { get; init; }
    public string Description { get; init; }
    public bool DefaultEnabled { get; init; } = true;
    public int PromptPriority { get; init; } = 50;
}
```

## Context 接口

### ILoopComponentContext

```csharp
public interface ILoopComponentContext
{
    string LoopId { get; }
    string LoopType { get; }  // "channel" / "system" / 自定义

    bool IsEnabled { get; }
    void Enable();
    void Disable();

    void WakeLoop();

    // 事件
    void PublishLocal<TEvent>(TEvent e) where TEvent : class;
    void PublishGlobal<TEvent>(TEvent e) where TEvent : class;
    void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
    void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;

    // 服务
    T? GetService<T>() where T : class;
    IPluginStorage Storage { get; }
}
```

### IGlobalComponentContext

```csharp
public interface IGlobalComponentContext
{
    bool IsEnabled { get; }
    void Enable();
    void Disable();

    void WakeLoop(string loopId);

    // 事件
    void PublishGlobal<TEvent>(TEvent e) where TEvent : class;
    void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
    void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;

    // 服务
    T? GetService<T>() where T : class;
    IPluginStorage Storage { get; }
}
```

## Attribute 声明

```csharp
[AttributeUsage(AttributeTargets.Class)]
public class ComponentAttribute : Attribute
{
    public string Name { get; set; }
    public ComponentScope Scope { get; set; } // Global / Loop
}

[AttributeUsage(AttributeTargets.Class)]
public class LoopApplicabilityAttribute : Attribute
{
    public Applicability Channel { get; set; } = Applicability.Enabled;
    public Applicability System { get; set; } = Applicability.Enabled;
}

public enum ComponentScope { Global, Loop }
public enum Applicability { Enabled, Disabled, NotApplicable }
```

### 工具可见性

```csharp
[AttributeUsage(AttributeTargets.Class)]
public class ToolVisibilityAttribute : Attribute
{
    public Visibility Default { get; set; } = Visibility.FollowState;
}

public enum Visibility { AlwaysVisible, FollowState, AlwaysHidden }
```

配置优先级：插件代码默认值 → 配置文件覆盖（可设为 AlwaysVisible / FollowState / AlwaysHidden）

## 事件系统

### 作用域

- **Local**：只在当前循环内传播（Loop → 同 Loop 的其他 Component）
- **Global**：全局可见（所有 Global Component + 所有 Loop Component 都能收到）

### 发布规则

| 发布者 | PublishLocal | PublishGlobal |
|--------|-------------|---------------|
| Loop Component | 当前循环内 | 全局所有人 |
| Global Component | N/A | 全局所有人 |

### 订阅规则

统一 `Subscribe<T>()`，同时接收 Local 和 Global 的同类型事件。如需区分来源，事件类型自带字段。

### 内置事件类型（SDK 预定义）

```csharp
public record MessageReceived(string LoopId, string SenderId, string Content);
public record LoopActivated(string LoopId, string Reason);
public record LoopPausing(string LoopId);
public record TaskArrived(string TaskId, string Description);
public record ComponentStateChanged(string ComponentName, bool IsEnabled, string LoopId);
```

循环宿主负责在适当时机发布这些事件。

## Component 状态管理

### Enabled / Disabled

- Component 始终存在（已实例化），但有启用/禁用状态
- Disabled 时：大部分工具不可见（FollowState 的隐藏），生命周期钩子不触发
- Enabled 时：所有 FollowState 工具可见，生命周期正常

### 适用性（Applicability）

插件声明对各循环类型的适用性：
- **Enabled**：适用，默认启用
- **Disabled**：适用，默认禁用（配置可改为启用）
- **NotApplicable**：不适用，不挂载，配置也无法覆盖

### 控制方式

1. **Component 自控**：通过 context.Enable() / context.Disable()
2. **组件管理器**：一个特殊的 ILoopComponent，提供 enable_component / disable_component 工具
3. **配置文件**：启动时默认状态

## 宿主集成

### PluginLoader 扫描

1. 扫描 DLL，找所有带 `[Component]` 的类型
2. 读取 Attribute 判断 Scope 和 Applicability
3. 注册到 ComponentRegistry（类型 + 元数据，不实例化）

### Global Component 实例化（MasterEngine.InitAsync）

1. 从 ComponentRegistry 取所有 Global 类型
2. 实例化，调 OnInitAsync(context)
3. 根据配置设置初始 Enabled/Disabled

### Loop Component 实例化（循环启动时）

1. 查 ComponentRegistry 中 Applicability != NotApplicable 的 Loop 类型
2. 读配置决定初始状态
3. 实例化，调 OnInitAsync(context)
4. 循环销毁时调 OnDestroyAsync()

### 每轮调用顺序

```
OnActivatedAsync（闲置→唤醒时，仅首轮）
  ↓
OnBeforeInvokeAsync
  ↓
[宿主收集 BuildPromptSection → 拼入 prompt]
[宿主收集可见工具列表 → 传给模型]
[模型调用 → 执行工具]
  ↓
OnAfterInvokeAsync
  ↓
[决定继续/暂停]
  ↓
OnPauseAsync（进入等待时）
```

## 迁移路径

### 现有 EngineModule → ILoopComponent

| 现有模块 | 迁移为 |
|----------|--------|
| DelegationModule | Plugin.DelegationTools 内的 ILoopComponent |
| SystemNotificationModule | 同上或独立插件 |
| PinboardModule | Plugin.WorkingTools 内的 ILoopComponent |
| ThinkingNotesModule | Plugin.WorkingTools 内的 ILoopComponent |
| RetainListModule | Plugin.WorkingTools 内的 ILoopComponent |
| LoopControlModule | 保留为内建（核心循环控制） |
| ToolStatusModule | 保留为内建或迁移 |

### 现有独立工具 → Component 内工具

所有现有插件（Plugin.BasicTools / Plugin.FileTools / Plugin.MemoryTools）改为 Component 形式：
- 大部分退化为纯工具容器（Global Component，不声明生命周期）
- 需要 prompt 注入的升级为 Loop Component

## 示例：DelegationComponent

```csharp
[Component(Name = "delegation", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.NotApplicable)]
public class DelegationComponent : ILoopComponent
{
    private ILoopComponentContext _ctx;
    private List<DelegationResult> _pendingResults = new();

    public ComponentMeta Meta => new()
    {
        Name = "delegation",
        Description = "频道循环委托管理",
        DefaultEnabled = true,
        PromptPriority = 30
    };

    public IEnumerable<ITool> Tools => [new DelegateTaskTool(this)];

    public async Task OnInitAsync(ILoopComponentContext context)
    {
        _ctx = context;
        context.Subscribe<DelegationCompleted>(OnDelegationCompleted);
    }

    private Task OnDelegationCompleted(DelegationCompleted e)
    {
        if (e.SourceLoopId == _ctx.LoopId)
        {
            _pendingResults.Add(e.Result);
            _ctx.WakeLoop();
        }
        return Task.CompletedTask;
    }

    public string? BuildPromptSection()
    {
        if (_pendingResults.Count == 0) return null;
        // 注入委托结果到 prompt
        return FormatResults(_pendingResults);
    }

    // ... 其他生命周期方法空实现
}
```
