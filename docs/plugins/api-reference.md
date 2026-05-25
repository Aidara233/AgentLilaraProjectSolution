# API 参考

## 接口

### ITool

所有工具的入口接口。

```csharp
public interface ITool
{
    string Name { get; }                              // 工具名（英文，AI 调用用）
    string Description { get; }                       // 功能描述（Prompt 中展示）
    IReadOnlyList<ToolParameter> Parameters { get; }  // 参数列表
    TimeSpan Timeout { get; }                         // 超时时间

    Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct);

    // 默认实现：从 Parameters 推导 JSON Schema
    JsonNode GetInputSchema();
}
```

### ILoopComponent

循环作用域组件。每个引擎循环（ChannelEngine / SystemEngine）各自创建一个实例。

```csharp
public interface ILoopComponent
{
    ComponentMeta Meta { get; }
    IEnumerable<ITool> Tools { get; }

    // 生命周期
    Task OnInitAsync(ILoopComponentContext context, InitReason reason);
    Task<ShutdownResponse> OnShutdownRequestedAsync(ShutdownReason reason);
    Task OnShutdownAsync(ShutdownReason reason);
    Task OnEnabledAsync();
    Task OnDisabledAsync();
    Task OnActivatedAsync();      // 引擎从暂停恢复
    Task OnPauseAsync();          // 引擎暂停

    // AI 轮次钩子
    Task OnBeforeInvokeAsync();
    Task OnAfterInvokeAsync();

    // Prompt 注入（null = 不注入）
    string? BuildPromptSection();
}
```

### IGlobalComponent

全局作用域组件。整个应用生命周期内只有一个实例，被所有循环共享。

```csharp
public interface IGlobalComponent
{
    ComponentMeta Meta { get; }
    IEnumerable<ITool> Tools { get; }

    Task OnInitAsync(IGlobalComponentContext context, InitReason reason);
    Task<ShutdownResponse> OnShutdownRequestedAsync(ShutdownReason reason);
    Task OnShutdownAsync(ShutdownReason reason);
    Task OnEnabledAsync();
    Task OnDisabledAsync();

    // LoopInfo 标识调用方（哪个循环）
    string? BuildPromptSection(LoopInfo caller);
}
```

### 基类

`LoopComponentBase` 和 `GlobalComponentBase` 提供了所有虚方法的空实现，只需覆写 `Meta`、`Tools` 和所需的生命周期方法。

### 上下文接口

**ILoopComponentContext**：

```csharp
public interface ILoopComponentContext
{
    string LoopId { get; }
    string LoopType { get; }         // "channel" / "system"

    bool IsEnabled { get; }
    void Enable();
    void Disable();

    void WakeLoop();                  // 通知引擎有新工作

    // 事件总线
    void PublishLocal<TEvent>(TEvent e) where TEvent : class;
    void PublishGlobal<TEvent>(TEvent e) where TEvent : class;
    void Subscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
    void Unsubscribe<TEvent>(Func<TEvent, Task> handler) where TEvent : class;

    T? GetService<T>() where T : class;
    IPluginStorage Storage { get; }
}
```

**IGlobalComponentContext**：

```csharp
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

**IToolContext**（供独立工具使用）：

```csharp
public interface IToolContext
{
    T? GetService<T>() where T : class;
    T Require<T>() where T : class;    // 服务不存在时抛异常
    IPluginStorage Storage { get; }
}
```

---

## 属性

### [Component]

标记组件类。**Name 必须全局唯一**。

| 属性 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `Name` | string | 必填 | 组件唯一标识 |
| `Scope` | ComponentScope | `Global` | 组件作用域 |

### [LoopApplicability]

声明组件在哪些类型的循环中可用。仅对 Loop 组件有效。

| 属性 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `Channel` | Applicability | `Enabled` | 频道循环中是否可用 |
| `System` | Applicability | `Enabled` | 系统循环中是否可用 |

### [ToolVisibility]

控制工具的默认可见性策略。

| 属性 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `Default` | Visibility | `FollowState` | AlwaysVisible / FollowState / AlwaysHidden |

### [ToolMeta]

标记工具类，声明运行行为元数据。

| 属性 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `Group` | string? | null | 工具组名（WebUI 分组，null=默认组始终可见） |
| `ContinueLoop` | bool | false | 执行后是否触发下一轮 AI |
| `AllowSubAgent` | bool | true | 子 agent 是否可使用此工具 |
| `CapabilitySummary` | string? | null | 能力摘要（Express 模式注入），null=不暴露 |
| `Permission` | ToolPermission | `Default` | 所需权限等级：Default / Elevated / Admin |
| `ExpressAvailable` | bool | false | Express 模式是否可用 |

### [PluginDependency]

声明对其他插件的依赖（**当前未强制执行**，仅供文档用途）。

```csharp
[PluginDependency("memory-tools")]
```

---

## 枚举

### ComponentScope

| 值 | 说明 |
|----|------|
| `Global` | 全局单例，整个应用一个实例 |
| `Loop` | 每个引擎循环一个实例 |

### Applicability

| 值 | 说明 |
|----|------|
| `Enabled` | 可用 |
| `Disabled` | 禁用 |
| `NotApplicable` | 不适用 |

### Visibility

| 值 | 说明 |
|----|------|
| `AlwaysVisible` | 始终可见 |
| `FollowState` | 跟随组件启用/禁用状态 |
| `AlwaysHidden` | 始终隐藏 |

### ShutdownReason

| 值 | 说明 |
|----|------|
| `Destroy` | 引擎销毁（正常退出） |
| `Reload` | 插件热重载 |

### InitReason

| 值 | 说明 |
|----|------|
| `Fresh` | 首次初始化 |
| `Reload` | 热重载后重新初始化 |

### ToolPermission

| 值 | 说明 |
|----|------|
| `Default` | 默认权限，所有工具可用 |
| `Elevated` | 需要提升权限 |
| `Admin` | 仅管理员 |

---

## 数据类型

### ComponentMeta

```csharp
public class ComponentMeta
{
    public required string Name { get; init; }      // 组件名
    public required string Description { get; init; } // 描述
    public bool DefaultEnabled { get; init; } = true;
    public int PromptPriority { get; init; } = 50;  // 注入优先级，越小越靠前
}
```

### ToolResult

```csharp
public class ToolResult
{
    public string Status { get; set; } = "success";  // "success" / "failed"
    public string? Data { get; set; }                 // 成功时的返回内容
    public string? Error { get; set; }                // 失败时的错误信息
    public List<ContentAttachment>? Attachments { get; set; }  // 图片等附件
    public bool IsSuccess => Status == "success";
}
```

### ToolParameter

```csharp
public class ToolParameter(string name, string description, int index, bool isRequired = true)
{
    string Name { get; }
    string Description { get; }
    int Index { get; }         // 参数位置（0-based）
    bool IsRequired { get; }
}
```

### ToolDefinition

供 AI API 使用的工具定义，由宿主根据 ITool 自动生成：

```csharp
public class ToolDefinition
{
    public string Name { get; set; }
    public string Description { get; set; }
    public JsonNode Parameters { get; set; }
}
```

### ShutdownResponse

```csharp
public class ShutdownResponse
{
    public bool CanShutdown { get; set; } = true;
    public string? Reason { get; set; }
    public static ShutdownResponse Ok => new();
}
```

---

## 服务

通过 `GetService<T>()` 获取。所有服务接口定义在 `AgentLilara.PluginSDK.Services` 命名空间下。

### IMemoryAccess

记忆系统的完整访问接口。提供 CRUD + 语义搜索能力。

```csharp
// 主要方法
Task<MemoryEntry?> GetByIdAsync(string id);
Task<List<MemoryEntry>> SearchAsync(string query, int limit = 10, ...);
Task<string> StoreAsync(MemoryEntry entry);
Task DeleteAsync(string id);
```

### IAgentMessaging

跨循环请求系统。用于频道循环和系统循环之间的通信。

```csharp
// 提交请求（fire-and-forget）
string SubmitFireAndForget(string? targetId, string title, string content);

// 接收待处理请求
List<CrossRequestInfo> Receive(int maxCount = 10);

// 回应请求
bool Respond(string requestId, CrossRequestResponseType type, string content);

// 查询
List<string> GetActiveLoopIds();
List<CrossRequestInfo> GetActiveRequests();
```

### ISubAgentAccess

子 agent 生命周期管理。

### IBeaconAccess

复盘信标。标记值得回顾的消息。

### IPersonAccess / IChannelAccess

人物数据和频道信息查询。

### IEventBusAccess

事件总线，用于组件间解耦通信。

### IEngineAccess / ILoopControl / ISleepAccess

引擎管理、循环模式控制、睡眠状态等系统服务。

### IToolHistoryAccess / ILogAccess

工具调用历史和日志查询。

---

## 存储

### IPluginStorage

每个插件实例获得绑定了自己路径的存储对象。

```csharp
public interface IPluginStorage
{
    string GlobalDirectory { get; }    // 插件全局目录（配置、共享数据）
    string InstanceDirectory { get; }  // 当前实例目录（循环隔离）
}
```

- Global 组件：`GlobalDirectory = InstanceDirectory`
- Loop 组件：`InstanceDirectory` 为每个循环独立（如 `per-channel-xxx/`）

路径示例：
```
Storage/Plugins/file-tools/          ← GlobalDirectory
Storage/Plugins/file-tools/per-channel-abc123/  ← InstanceDirectory (Loop)
```

---

## Prompt 注入

组件可通过两种方式向 AI prompt 注入内容：

1. **BuildPromptSection()** — 组件接口方法，每轮调用，返回注入文本
2. **IPromptContributor** — 独立接口，适用于无组件的工具

```csharp
public interface IPromptContributor
{
    string SectionKey { get; }   // 唯一标识（去重）
    int Priority { get; }        // 注入优先级，越小越靠前
    string? BuildSection();      // 返回 null = 本轮不注入
}
```

---

## 生命周期流程

```
启动 / 热重载
  │
  ├─ 组件实例化（PluginLoader 发现 + 构造函数注入）
  └─ OnInitAsync(context, InitReason)   ← 在此启动后台任务、初始化状态
       │
       │  ⚠ OnEnabledAsync() 在此处不会被自动调用！
       │  仅在组件通过 context.Enable() 从禁用→启用时触发。
       │
       ▼
  ┌── AI 循环 ──────────────────┐
  │  ├─ OnBeforeInvokeAsync()   │
  │  ├─ BuildPromptSection()    │
  │  ├─ 工具执行                 │
  │  └─ OnAfterInvokeAsync()    │
  ├─────────────────────────────┤
  │  OnActivatedAsync() ← 引擎恢复 │
  │  OnPauseAsync()     ← 引擎暂停 │
  └─────────────────────────────┘
       │
       ▼
  OnDisabledAsync()
  OnShutdownRequestedAsync(reason) → ShutdownResponse
  OnShutdownAsync(reason)
```

> **重要**：`OnEnabledAsync()` 在组件首次初始化时**不会被调用**。`ComponentHost.InitAsync()` 只调 `OnInitAsync()` 然后直接注册工具。如果需要在组件启用时执行初始化逻辑（如启动定时器），请放在 `OnInitAsync()` 中。`OnEnabledAsync()` 保留用于组件从禁用状态恢复的场景。
```

**注意**：`OnShutdownRequestedAsync` 可以返回 `new ShutdownResponse { CanShutdown = false, Reason = "..." }` 来拒绝关闭（宿主有 30s 超时保护）。

---

## 构造函数注入

PluginLoader 按以下优先级选择构造函数：

1. 含 `IToolContext` 参数的构造函数（独立工具模式）
2. 含 `IPluginStorage` 参数的构造函数（Component 模式）
3. 无参构造函数

**推荐**：在 Component 管理的工具中使用 `IPluginStorage` 构造函数；无组件独立工具使用 `IToolContext`。
