# API 参考

## 扩展点接口（插件实现的接口）

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

循环作用域组件。每个引擎循环（ChannelEngine / SystemEngine / ReviewEngine / SubAgent）各自创建一个实例。

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

### IPromptContributor

独立 prompt 注入接口，适用于无组件的场景。

```csharp
public interface IPromptContributor
{
    string SectionKey { get; }   // 唯一标识（去重）
    int Priority { get; }        // 注入优先级，越小越靠前
    string? BuildSection();      // 返回 null = 本轮不注入
}
```

### IWebUIProvider

WebUI 页面贡献接口。贡献的页面自动出现在导航中。

```csharp
public interface IWebUIProvider
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<PageDefinition> Pages { get; }
}
```

### IInjectProvider

引擎级 prompt 注入接口（`AgentCoreProcessor.Engine` 命名空间）。

```csharp
public interface IInjectProvider
{
    int InjectPriority { get; }
    Task<string?> BuildStartInjectAsync(InjectContext ctx);
    Task<string?> BuildRoundInjectAsync(InjectContext ctx);
}

// InjectContext 携带：
//   string Mode           — "express" / "working"
//   int CurrentRound      — 当前轮次
//   int MaxRounds         — 最大轮次
//   int EstimatedTokens   — 估算 token 数
```

### IEngineLifecycle

引擎生命周期钩子（`AgentCoreProcessor.Engine` 命名空间）。

```csharp
public interface IEngineLifecycle
{
    Task OnInitializeAsync(IServiceProvider services);
    Task OnShutdownAsync();
}
```

---

## 基类

`LoopComponentBase` 和 `GlobalComponentBase` 提供了所有虚方法的空实现，只需覆写 `Meta`、`Tools` 和所需的生命周期方法。

---

## 上下文接口（宿主提供给插件）

### ILoopComponentContext

```csharp
public interface ILoopComponentContext
{
    string LoopId { get; }
    string LoopType { get; }         // "channel" / "system" / "review" / "sub-agent"

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

### IGlobalComponentContext

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

### IToolContext

供独立工具（无组件）使用，通过构造函数注入。

```csharp
public interface IToolContext
{
    T? GetService<T>() where T : class;
    T Require<T>() where T : class;    // 服务不存在时抛异常
    IPluginStorage Storage { get; }
}
```

### IPageContext

WebUI 页面运行时上下文，在页面组件中注入。

```csharp
public interface IPageContext
{
    void Emit(string eventName, JsonNode? payload = null);
    IDisposable On(string eventName, Action<JsonNode?> handler);
    JsonNode? GetState(string key);
    void SetState(string key, JsonNode? value);
    void Navigate(string route);
}
```

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

## 服务接口

通过 `GetService<T>()` 获取。所有服务接口定义在 `AgentLilara.PluginSDK.Services` 命名空间下（`ILogAccess` / `ISignalLogger` 在 `Logging` 命名空间）。

### IMemoryAccess

记忆系统的完整访问接口。

```csharp
// ===== 主记忆库 =====
Task<int> StoreAsync(MemoryWriteRequest request);
Task<List<MemoryEntry>> SemanticSearchAsync(string query, int limit = 20,
    int? personId = null, int? channelId = null);
Task<List<MemoryEntry>> FilterAsync(MemoryFilter filter);
Task<List<MemoryEntry>> ListAsync(int offset = 0, int limit = 100);
Task<int> CountAsync();
Task<MemoryEntry?> GetByIdAsync(int id);
Task DeleteAsync(int id);
Task UpdateAsync(int id, string newContent);      // 自动重算 embedding

// ===== 关联图 =====
Task<List<MemoryEntry>> GetLinkedAsync(int memoryId);
Task LinkAsync(int fromId, int toId, float strength = 1.0f);
Task UnlinkAsync(int fromId, int toId);

// ===== 向量操作 =====
Task<float[]?> GetEmbeddingAsync(int memoryId);
Task<float[]> ComputeEmbeddingAsync(string text);  // 纯计算，不存储
Task<List<MemoryEntry>> VectorSearchAsync(float[] embedding, int limit = 20,
    int? personId = null, int? channelId = null);

// ===== 临时记忆库 =====
Task<int> StoreTempAsync(TempMemoryWriteRequest request);
Task<List<TempMemoryEntry>> SearchTempAsync(string query, int limit = 20);
Task<List<TempMemoryEntry>> ListTempAsync(int offset = 0, int limit = 100);
Task<int> CountTempAsync();
Task DeleteTempAsync(int id);
```

关联 DTO：

```csharp
class MemoryEntry {
    int Id; string Content; string? Type; string? Subject;
    int? PersonId; int? ChannelId; float Importance; string? Confidence;
    bool IsPersistent; DateTime CreatedAt; DateTime? ExpiresAt; float Score;
}
class TempMemoryEntry {
    int Id; string Content; string? Type; string? Subject;
    int? PersonId; int? ChannelId; DateTime CreatedAt; float Score;
}
class MemoryWriteRequest {
    string Content; string? Type; string? Subject; int? PersonId;
    int? ChannelId; float Importance; string Confidence; bool IsPersistent;
    DateTime? ExpiresAt;
}
class TempMemoryWriteRequest {
    string Content; string? Type; string? Subject;
    int? PersonId; int? ChannelId;
}
class MemoryFilter {
    int? PersonId; int? ChannelId; string? Type; string? Subject;
    string? KeywordContains; DateTime? CreatedAfter; DateTime? CreatedBefore;
    float? MinImportance; int Offset; int Limit;
}
```

### IChannelAccess

频道信息和消息接口。

```csharp
Task<List<ChannelSummary>> GetAllChannelsAsync();
Task<ChannelDetail?> GetChannelDetailAsync(int channelId);
Task<List<MessageSummary>> GetMessagesAsync(int channelId, int count = 20);
Task UpdateAffinityAsync(int channelId, float delta);

// 消息输出（支持 <img hash="..." /> <at user="name"/> <reply id="xxx"/> 标签）
Task<string?> SendMessageAsync(int channelId, string content);
Task<string?> SendMediaAsync(int channelId, string mediaType, string identifier);
Task<string?> SendFileAsync(int channelId, string filePath, string? fileName = null);
```

关联 DTO：

```csharp
class ChannelSummary {
    int Id; string? Name; string Platform; int MessageCount; bool HasActiveEngine;
}
class ChannelDetail {
    int Id; string? Name; string Platform; string PlatformChannelId; int MessageCount;
}
class MessageSummary {
    long Id; string UserName; string Content; DateTime Timestamp;
}
```

### IAgentMessaging

跨循环请求系统，所有循环间通信的唯一入口。

```csharp
// 提交请求并等待首个回应（保留给特殊场景）
Task<CrossRequestResult> SubmitAndWaitAsync(
    string? targetId, string title, string content,
    Dictionary<string, string>? metadata = null, TimeSpan? timeout = null);

// Fire-and-forget 提交，状态变更通过通知队列送达
string SubmitFireAndForget(string? targetId, string title, string content);

// 当前循环读取待处理请求
List<CrossRequestInfo> Receive(int maxCount = 10);

// 回应请求
bool Respond(string requestId, CrossRequestResponseType type, string content);

// 排出委托状态变更通知（由 inject 模块消费）
List<DelegationNotificationInfo> DrainNotifications();

// 查询
List<CrossRequestInfo> GetActiveRequests();
List<CrossRequestInfo> GetCompletedRequests();
List<CrossRequestInfo> GetArchivedRequests();
CrossRequestInfo? Get(string requestId);

// 生命周期
void Archive(string requestId);
void Ignore(string requestId);        // 仅广播可用

// 获取所有活跃循环 ID
List<string> GetActiveLoopIds();
```

关联 DTO：

```csharp
enum CrossRequestResponseType { Accept, Reject, Progress, Complete, Failed, Ignore }

class CrossRequestResult {
    string RequestId; bool Success; bool TimedOut; string? Verdict; string? Result;
}
class CrossRequestInfo {
    string Id; string InitiatorId; string? TargetId; string Title; string Content;
    string State; DateTime SubmittedAt; DateTime ExpiresAt; DateTime? CompletedAt;
    List<CrossRequestResponseInfo> Responses;
}
class CrossRequestResponseInfo {
    int Seq; string ResponderId; string Type; string Content; DateTime Timestamp;
}
class DelegationNotificationInfo {
    string RequestId; string Title; string NewState; string ResponseType;
    string? ResponderId; string? Content; DateTime Timestamp;
}
```

### IEngineAccess

引擎管理接口。

```csharp
List<EngineSummary> GetActiveEngines();
void RequestStopByType(string engineType);
bool HasActive(string engineType);
```

### IEventBusAccess

事件总线，组件间解耦通信。

```csharp
void PublishSignal(string signal, string? source = null);
void Subscribe<T>(Action<T> handler) where T : class;
void Unsubscribe<T>(Action<T> handler) where T : class;
```

### ISleepAccess

睡眠状态接口。

```csharp
SleepLevel CurrentState { get; }
void SetState(SleepLevel level);

// SleepLevel 枚举：None=0, Wandering=1, Napping=2, DeepSleep=3
```

### IAdapterAccess

适配器操作接口（平台交互）。

```csharp
string? GetBotPlatformId(string platform);
Task<string?> ExecuteActionAsync(string adapterId, string action, string? paramsJson = null);
List<AdapterActionInfo> GetAvailableActions(string adapterId);
string? GetAdapterIdForChannel(string channelId);
```

关联 DTO：

```csharp
class AdapterActionInfo {
    string Name; string Label; string Description; List<ActionParamInfo> Params;
}
class ActionParamInfo {
    string Name; string Label; string Type; bool Required;
}
```

`GetAvailableActions()` 返回适配器支持的完整操作列表（标签、描述、参数元数据）。`GetAdapterIdForChannel()` 按频道 ID 查找对应的适配器实例 ID。`ExecuteActionAsync` 的 `paramsJson` 为 JSON 对象字符串如 `{"user_id":"123456"}`。

### ILoopControl

循环模式控制。

```csharp
EngineMode CurrentMode { get; }
void SetMode(EngineMode mode, string? reason = null);
void Signal();                       // 唤醒循环
```

### IPersonAccess

人物数据查询和更新。

```csharp
Task<List<PersonSummary>> GetAllAsync();
Task<PersonDetail?> GetByIdAsync(int id);
Task UpdateNameAsync(int id, string name, string? aliases = null);
Task UpdateFastMemoryAsync(int id, string fastMemory);
Task<List<PersonSummary>> GetByChannelAsync(int channelId);
```

关联 DTO：

```csharp
class PersonSummary {
    int Id; string? Name; string? Aliases; string? FastMemory;
    int TrustLevel; float TrustProgress; int AlertLevel;
}
class PersonDetail : PersonSummary { List<UserAccount> Accounts; }
class UserAccount {
    string Platform; string PlatformId; string? DisplayName;
}
```

### ISubAgentAccess

子 agent 生命周期管理（系统循环用）。

```csharp
SubAgentInfo Create(string instruction);
SubAgentInfo Create(string instruction, string? delegationId);
SubAgentInfo? Get(string sessionId);
Task<bool> SendInstructionAsync(string sessionId, string instruction);
void RequestStop(string sessionId);
List<SubAgentInfo> List();
```

### IBeaconAccess

复盘信标接口。频道循环通过此接口标记需要复盘关注的内容。

```csharp
Task CreateAsync(string reason, int? channelId = null, int? personId = null, int? messageId = null);
```

### IToolHistoryAccess

工具执行历史查询。

```csharp
ToolExecutionRecord? GetById(string callId);
List<ToolExecutionRecord> GetRecent(string? toolName = null, int count = 10);
```

关联 DTO：

```csharp
class ToolExecutionRecord {
    string CallId; string ToolName; List<string> Inputs;
    string Status; string? Data; string? Error; DateTime Timestamp;
}
```

### IReviewAccess

ReviewEngine 暴露给复盘工具的完整接口。

```csharp
// 游标
int? CursorMessageId { get; }
int? CursorChannelId { get; }
void MoveCursor(int? messageId, int? channelId);

// 消息读取
Task<List<ReviewMessageDto>> BrowseAsync(int count);
Task<List<ReviewMessageDto>> SearchMessagesAsync(
    string? query, int? channelId, int? personId,
    string? timeStart, string? timeEnd, int limit);

// 人物
Task<ReviewPersonDto?> GetPersonAsync(int personId);

// 信标
Task<List<ReviewBeaconDto>> GetUnprocessedBeaconsAsync();

// 评价缓冲
void AddEvaluation(string targetType, int targetId, string dimension, string rating);

// 思考笔记
string ThinkingNotes { get; set; }

// 进度
void SaveProgress();
void ClearProgress();

// 行动日志
Task LogActionAsync(string actionType, string summary, string? detailJson = null);

// 访问追踪
void TrackChannel(int channelId);
void TrackPerson(int personId);
```

### IReviewControl

复盘生命周期控制。

```csharp
bool IsCompleted { get; }
bool WakeNotified { get; }
bool ReserveGranted { get; }
bool RequestReinforcement();
void MarkComplete();
```

### ISignalLogger / ILogAccess

信号日志接口（`AgentLilara.PluginSDK.Logging` 命名空间）。

```csharp
// ISignalLogger — 写入
IDisposable Open(string group, string name, object? detail = null);
void Event(string group, string name, object? detail = null);
void Debug(string group, string name, object? detail = null);
void Warn(string group, string name, object? detail = null);
void Error(string group, string name, object? detail = null);

// ILogAccess : ISignalLogger — 查询
List<LogEventInfo> GetBySignal(string signalId);
List<LogEventInfo> GetByScope(string scope, long? since = null, int limit = 200);
List<LogEventInfo> GetRecent(int limit = 200, string? group = null, int? minLevel = null);
List<OpenSpanSummary> GetOpenSpans();
List<LogEventInfo> GetSignalList(int limit = 50);
List<TokenUsageInfo> GetTokenUsage(long? since = null, string? model = null, string? callerTag = null);
IDisposable Subscribe(Action<IReadOnlyList<LogEventInfo>> callback);
void Cleanup(int? retainDays = null);
```

### IDataSource

WebUI 卡片数据源接口（`AgentLilara.PluginSDK.WebUI` 命名空间）。

```csharp
Task<DataResult> FetchAsync(DataQuery? query = null, CancellationToken ct = default);
Task<ActionResult> SubmitAsync(string action, JsonNode? data = null, CancellationToken ct = default);
bool SupportsPush { get; }
IDisposable? Subscribe(Action<JsonNode?> callback);
```

### IDelegationAccess

> **已废弃**，由 `IAgentMessaging` 取代。14 个方法，含委托提交/评估/生命周期/重试。

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
| `Review` | Applicability | `Enabled` | 复盘循环中是否可用 |
| `SubAgent` | Applicability | `Enabled` | 子 agent 中是否可用 |

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
| `OutputOnly` | bool | false | 纯输出工具，不触发下一轮 |

### [PluginDependency]

声明对其他插件的依赖（**当前未强制执行**，仅供文档用途）。

```csharp
[PluginDependency("memory-tools")]
```

### [WebUIProvider]

标记 IWebUIProvider 类。

| 属性 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `BuiltIn` | bool | false | 是否为内置 Provider（非插件） |

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

### EngineMode

| 值 | 说明 |
|----|------|
| `Express` | 快速模式（fire-and-forget，不续轮） |
| `Working` | 工作模式（多轮推理） |

### SleepState

系统级睡眠状态，插件可据此决定行为。

| 值 | 说明 |
|----|------|
| `None` | 清醒 |
| `Daydream` | 走神（被 @ 即可唤醒） |
| `Nap` | 小睡（需 @ + 关键词叫醒） |
| `DeepSleep` | 大睡（仅管理员/任务可唤醒） |

### SleepLevel

引擎级睡眠级别（`ISleepAccess` 使用）。

| 值 | 说明 |
|----|------|
| `None` (0) | 未睡眠 |
| `Wandering` (1) | 走神 |
| `Napping` (2) | 小睡 |
| `DeepSleep` (3) | 深度睡眠 |

### CrossRequestResponseType

| 值 | 说明 |
|----|------|
| `Accept` | 接受请求 |
| `Reject` | 拒绝请求 |
| `Progress` | 进度更新 |
| `Complete` | 完成 |
| `Failed` | 失败 |
| `Ignore` | 忽略（仅广播） |

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

### ContentAttachment

工具结果附件（多模态内容）。

```csharp
public class ContentAttachment
{
    public string Type { get; set; } = "image";       // "image" 等
    public string? Base64Data { get; set; }            // Base64 编码数据
    public string? MediaType { get; set; }             // MIME 类型
    public string? FilePath { get; set; }              // 文件路径
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

### ToolCall

工具调用数据结构。

```csharp
public class ToolCall
{
    [JsonProperty("tool")] string Tool { get; set; }
    [JsonProperty("inputs")] List<string> Inputs { get; set; }
    [JsonIgnore] int Index { get; set; }
    [JsonIgnore] string? ToolUseId { get; set; }      // 原生 tool_use id
    [JsonIgnore] string? RawInputJson { get; set; }   // 原生 tool_use 原始 input JSON

    static ToolCall FromJson(string json);
    IEnumerable<string> Validate();
}
```

### ShutdownResponse

```csharp
// record ShutdownResponse(bool Allow, string? Reason)
public record ShutdownResponse(bool Allow, string? Reason);
public static ShutdownResponse Ok { get; }
public static ShutdownResponse NotReady(string reason) { get; }
```

### LoopInfo

```csharp
public record LoopInfo(string LoopId, string LoopType);
```

---

## WebUI 类型

### PageDefinition

```csharp
class PageDefinition {
    required string Route;
    required PageMeta Meta;
    required IReadOnlyList<CardDefinition> Cards;
    required IReadOnlyList<DataSourceDefinition> DataSources;
    PageLayoutType LayoutType;               // Grid / Sidebar
}

class PageMeta {
    required string Title; string? Icon; string? Group;
    int Order; bool DefaultCollapsed; bool ShowInNav = true;
}

enum PageLayoutType { Grid, Sidebar }
```

### CardDefinition

```csharp
class CardDefinition {
    required string Id;
    required CardType Type;
    string? DataSourceId;
    required CardSchema Schema;
    CardLayout Layout = new();
    string? Title;
    string? RowSelectEvent;            // 行选中事件名
}
```

### CardSchema 子类

| 类型 | JSON 标识 | 用途 |
|------|-----------|------|
| `TableSchema` | `"table"` | 数据表格（列定义、搜索、分页、行操作、过滤器） |
| `StatusSchema` | `"status"` | 状态字段展示（文本/徽章/进度/指示器） |
| `FormSchema` | `"form"` | 表单（多类型字段、分组、提交/重置按钮） |
| `StreamSchema` | `"stream"` | 流式输出（自动滚动、暂停、过滤） |
| `ChatSchema` | `"chat"` | 聊天界面（发送者切换、输入框、自动滚动） |
| `TreeSchema` | `"tree"` | 树形结构（节点 ID/标签/父级/子级字段、可折叠） |
| `DetailSchema` | `"detail"` | 详情展示（分区 + 字段列表、可折叠） |
| `ActionCardSchema` | `"action"` | 操作卡片（带参数的操作提交） |
| `PropertyGridSchema` | `"property-grid"` | 属性编辑（支持敏感字段掩码、只读） |

### DataSource

```csharp
interface IDataSource {
    Task<DataResult> FetchAsync(DataQuery? query, CancellationToken ct);
    Task<ActionResult> SubmitAsync(string action, JsonNode? data, CancellationToken ct);
    bool SupportsPush { get; }
    IDisposable? Subscribe(Action<JsonNode?> callback);
}

class DataQuery {
    int? Page; int? PageSize; string? Search; string? SortBy; bool SortDesc;
    List<DataFilter>? Filters; JsonNode? Extra; Dictionary<string, string>? RouteParams;
}
class DataResult { required JsonNode Data; int? TotalCount; JsonNode? Meta; }
class ActionResult { bool Success; string? Message; JsonNode? Data; }
class DataFilter { required string Field; required string Operator; required string Value; }
```

---

## 内置事件

命名空间 `AgentLilara.PluginSDK.Events`，用于 `Subscribe<T>()` / `PublishLocal/Global<T>()`。

```csharp
public record MessageReceived(string LoopId, string SenderId, string Content);
public record LoopActivated(string LoopId, string Reason);
public record LoopPausing(string LoopId);
public record TaskArrived(string TaskId, string Description);
public record ComponentStateChanged(string ComponentName, bool IsEnabled, string LoopId);
```

---

## 构造函数注入

PluginLoader 按以下优先级选择构造函数：

1. 含 `IToolContext` 参数的构造函数（独立工具模式）
2. 含 `IPluginStorage` 参数的构造函数（Component 模式）
3. 无参构造函数

**推荐**：在 Component 管理的工具中使用 `IPluginStorage` 构造函数；无组件独立工具使用 `IToolContext`。

---

## 生命周期流程

```
启动 / 热重载
  │
  ├─ 组件实例化（PluginLoader 发现 + 构造函数注入）
  └─ OnInitAsync(context, InitReason)   ← 在此启动后台任务、初始化状态
       │
       │  ⚠ OnEnabledAsync() 在首次初始化时不会被调用！
       │  仅通过 context.Enable() 从禁用→启用时触发。
       │  初始化逻辑请放在 OnInitAsync() 中。
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

**注意**：`OnShutdownRequestedAsync` 可以返回 `ShutdownResponse.NotReady("reason")` 来拒绝关闭（宿主有 30s 超时保护）。
