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

循环作用域组件。每个会话（RuntimeSession）各自创建一个实例。

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

    // 退出询问钩子：所属 session 准备 end_session 时被询问，返回 false 阻止退出（宿主注入提醒文本）
    Task<bool> OnCanExitAsync();
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

    // LoopInfo 标识调用方（哪个会话）
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
//   string Mode           — "express" / "working"（宿主侧概念，插件不应依赖）
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
    string SessionId { get; }        // 当前会话 sessionId（裸，无 "session:" 前缀；LoopId 概念已移除）
    string LoopType { get; }         // "channel" / "task" / "express"（对应会话类型档案）

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

    void WakeLoop(string sessionId);

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
    string GlobalDirectory { get; }      // 插件全局目录（配置、共享数据）
    string InstanceDirectory { get; }    // 当前实例目录（循环隔离）
    string WorkspaceDirectory { get; }   // 共享工作区目录（Storage/Workspace/）
}
```

- **GlobalDirectory**：`Storage/PluginData/{name}/`，该组件所有实例共享（配置、共享数据）
- **InstanceDirectory**：按**裸 sessionId** 隔离。Loop 组件为 `Storage/PluginData/{name}/{sessionId}`；Global 组件固定 `Storage/PluginData/{name}/_global`
- **WorkspaceDirectory**：所有文件工具共享的沙箱目录（`Storage/Workspace/`）。插件内需要访问 Workspace 时，优先使用此属性，**不要**通过 `GlobalDirectory` 做 `..` 相对跳转。

路径示例：
```
Storage/PluginData/file-tools/         ← GlobalDirectory
Storage/PluginData/file-tools/_global  ← InstanceDirectory (Global)
Storage/PluginData/file-tools/3f2a9c   ← InstanceDirectory (Loop，裸 sessionId)
Storage/Workspace/                     ← WorkspaceDirectory（所有文件插件共用）
```

---

## 服务接口

通过 `GetService<T>()` 获取。所有服务接口定义在 `AgentLilara.PluginSDK.Services` 命名空间下（`ILogAccess` / `ISignalLogger` 在 `Logging` 命名空间）。

**完整服务接口清单**（`Services/` 下共 17 个接口 + `Product` DTO）：IAdapterAccess、IBeaconAccess、IChannelAccess、IConnectedChannel、ICurrencyService、IEngineAccess、IEventBusAccess、IImageAccess、IMemoryAccess、IPersonAccess、IProductRegistry、IReviewAccess、IReviewControl、ISessionResourceAccess、ISpeakGuard、ISubAgentAccess、IToolHistoryAccess。其中 IConnectedChannel / ICurrencyService / IImageAccess / ISpeakGuard / ISessionResourceAccess / IProductRegistry 的签名见下文「补充服务接口」。

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
Task<List<LinkedMemoryEntry>> GetLinkedAsync(int memoryId);
Task LinkAsync(int fromId, int toId, float relevance = 1.0f, float support = 1.0f, string linkType = "semantic");
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
    int? PersonId; int? ChannelId; float Importance; float Certainty;
    int RecallCount; DateTime? LastRecalledAt; bool IsSuperseded;
    bool IsPersistent; DateTime CreatedAt; DateTime? ExpiresAt; float Score;
}
class TempMemoryEntry {
    int Id; string Content; string? Type; string? Subject;
    int? PersonId; int? ChannelId; DateTime CreatedAt; float Score;
}
class MemoryWriteRequest {
    string Content; string? Type; string? Subject; int? PersonId;
    int? ChannelId; float Importance; float Certainty; bool IsPersistent;
    DateTime? ExpiresAt;
}
class TempMemoryWriteRequest {
    string Content; string? Type; string? Subject;
    int? PersonId; int? ChannelId;
}
class MemoryFilter {
    int? PersonId; int? ChannelId; string? Type; string? Subject;
    string? KeywordContains; DateTime? CreatedAfter; DateTime? CreatedBefore;
    float? MinImportance; float? MinCertainty; int Offset; int Limit;
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

### 补充服务接口

以下接口定义在 `AgentLilara.PluginSDK.Services`，较常用，集中速查：

```csharp
// IConnectedChannel — 当前 session 连接的频道状态（per-session 服务）
// 发送工具（speak 等）读它决定目标频道；连接工具（connect_channel 等）通过 session 改它
int CurrentChannelId { get; }   // 当前连接的频道 ID，0 = 未连接
ConnectionMode Mode { get; }    // None / Listen / Full（enum：旁听 / 占用）
bool CanSpeak { get; }          // 仅 Full 连接态可发言

// ICurrencyService — bot 内部单账户，资源消费 + 商品购买
decimal Balance { get; }
CurrencyTransaction Spend(decimal amount, string purpose);   // 余额不足抛 InvalidOperationException
CurrencyTransaction Grant(decimal amount, string reason);    // 拨款（管理员操作）
IReadOnlyList<CurrencyTransaction> GetTransactions(int limit = 50);

// IImageAccess — 图片解析 + OCR（桥接内部 ImageStorage）
Task<ImageResolveResult> ResolveImageAsync(string identifier, string source = "workspace");
Task<ImageOcrResult> OcrImageAsync(string identifier, string source = "workspace");  // 始终调 API
Task<ImageOcrResult> GetOcrAsync(string identifier, string source = "workspace");    // 只用缓存

// ISpeakGuard — speak 防话唠守卫（追踪连续纯发言轮次）
int ConsecutiveSpeakRounds { get; }
bool IsBlocked { get; }

// ISessionResource — 会话资源接口 + ISessionResourceAccess 注册表
// 子 agent / 下载 / SSH 等 session-scoped 资源实现它；end_session 时 HasActiveResources=true 会阻止退出
Task OnSessionCompactingAsync(string oldSessionId, string newSessionId);
Task OnSessionFreedAsync(string sessionId);
bool HasActiveResources(string sessionId);
// ISessionResourceAccess:
void Register(ISessionResource resource);
void Unregister(ISessionResource resource);
string? ResolveActiveSession(string sessionId);   // 经池树解出活跃 session

// IProductRegistry — 商品注册表（插件 OnInitAsync 中注册商品）
void Register(Product product);
void Unregister(string productId);
IReadOnlyList<Product> GetAll();
Product? GetById(string id);
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

子 agent 生命周期管理。

```csharp
SubAgentInfo Create(string instruction, SubAgentOptions? options = null);
SubAgentInfo? Get(string sessionId);
Task<bool> SendInstructionAsync(string sessionId, string instruction);
SubAgentStopResult RequestStop(string sessionId);
List<SubAgentInfo> List(string? parentSessionId = null);
SubAgentDetail? GetDetail(string sessionId);
bool ReportToParent(string callerSessionId, string content, ReportState state);
Task<bool> WaitAsync(string sessionId, int timeoutMs);
```

### IBeaconAccess

信标访问接口。任何人都能创建信标，消费者按 `Consumer` 字段过滤属于自己的信标。

```csharp
Task<int> CreateAsync(string content, string source, string consumer,
    int? channelId = null, int? personId = null, int? messageId = null);
Task<List<BeaconDto>> GetUnprocessedAsync(string consumer);
Task MarkProcessedAsync(int id);
```

关联 DTO：

```csharp
class BeaconDto {
    int Id; int? MessageId; int? ChannelId; int? PersonId;
    string Content; string Source; string Consumer; string CreatedAt;
}
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

// 消息定位
Task<ReviewMessageDto?> GetMessageByIdAsync(int messageId);
Task<int?> GetMessageOffsetAsync(int channelId, int baseMessageId, int offset);

// 人物特质
Task<List<PersonTraitDto>> GetPersonTraitsAsync(int personId, string? category = null);
Task UpsertPersonTraitAsync(int personId, string category, string key, string value, float confidence, string? sourceHint = null);

// 信任操作
Task<TrustCriteriaDto> GetTrustCriteriaAsync(int personId);
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

> **已删除**。原委托系统（CrossRequestRegistry + 8 状态机 + JSONL 持久化）已整体移除；其后的跨循环单发消息接口 `IAgentMessaging` 亦已移除。跨循环通讯已废弃，session 间通讯走子 agent 邮箱（`ISubAgentAccess`）。

---

## 属性

### [Component]

标记组件类。**Name 必须全局唯一**。

| 属性 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `Name` | string | 必填 | 组件唯一标识 |
| `Scope` | ComponentScope | `Global` | 组件作用域 |

### [ToolMeta]

标记工具类，声明运行行为元数据。

| 属性 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `Group` | string? | null | 工具组名（WebUI 分组，null=默认组始终可见） |
| `ContinueLoop` | bool | false | 执行后是否触发下一轮 AI |
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

> 宿主侧仍有 Express/Working 模式枚举（`AgentCoreProcessor.Engine.EngineMode`），但不属于插件契约。

### SleepState

系统级睡眠状态，插件可据此决定行为。

| 值 | 说明 |
|----|------|
| `None` | 清醒 |
| `Daydream` | 走神（被 @ 即可唤醒） |
| `Nap` | 小睡（需 @ + 关键词叫醒） |
| `DeepSleep` | 大睡（仅管理员/任务可唤醒） |

> 注：引擎级 `SleepLevel` 枚举（`ISleepAccess`）已随睡眠接口一并删除，此处只保留系统级 `SleepState`。

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
public record LoopInfo(string SessionId, string LoopType);
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
    string? LinkEvent;                 // 行选中/值变化时发出的事件名（Table/Tree/Form）
    string? ListenEvent;               // 监听的事件名，触发时以 payload 作为 Extra 刷新数据源
    string? InjectEvent;               // 提交时从 PageContext state 取此 key 的值合并进 payload
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

`CardType` 枚举共 10 个值：Table / Status / Form / Stream / Chat / Tree / Detail / Action / PropertyGrid / **Custom**（自定义扩展预留）。

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
public record MessageReceived(string SessionId, string SenderId, string Content);
public record LoopActivated(string SessionId, string Reason);
public record LoopPausing(string SessionId);
public record TaskArrived(string TaskId, string Description);
public record ComponentStateChanged(string ComponentName, bool IsEnabled, string SessionId);
```

---

## 构造函数注入

PluginLoader 按**构造参数个数降序**尝试所有构造函数，逐个参数按以下顺序解析：

1. 内置 resolver 字典：`Engine.EventBus` / `Engine.ModuleBus` / `Engine.Gate` / `IMemoryAccess`
2. `IServiceProvider`
3. 从 `IServiceProvider.GetService(type)` 按类型解析（含 `IToolContext`、`IPluginStorage` 等服务）

首个参数全部可解析的构造函数被选中。**推荐**：工具类用无参构造或仅注入所需服务接口；组件类用 `IPluginStorage` 构造。

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
