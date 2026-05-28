# 插件实现模式

> 从现有插件中提炼的可复用设计模式。新 Claude 写插件前先读这个，避免踩坑。

---

## 1. Static Accessor Bridge — 全局组件与循环组件通信

### 问题

Global 组件（全局单例）和 Loop 组件（每循环一个实例）不能互相引用。但有些场景需要跨作用域协作：
- Global 组件持有共享资源（HttpClient、下载存储），Loop 组件需要使用
- Global 组件的后台工作完成后，需要通知特定 Loop 组件

### 方案

定义一个**静态 Accessor 类**，Global 组件在 `OnInitAsync` 中配置，Loop 组件通过静态属性读取。

```csharp
// 1. 定义静态桥接类（放在 Global 和 Loop 都能访问的位置）
public static class NetworkToolsAccessor
{
    public static HttpClient? HttpClient { get; private set; }
    public static DownloadStore? Store { get; private set; }

    public static void Configure(HttpClient http, DownloadStore store)
    {
        HttpClient = http;
        Store = store;
    }
}

// 2. Global 组件初始化时配置
public override Task OnInitAsync(IGlobalComponentContext ctx, InitReason reason)
{
    var http = new HttpClient();
    var store = new DownloadStore(ctx.Storage.GlobalDirectory);
    NetworkToolsAccessor.Configure(http, store);
    return Task.CompletedTask;
}

// 3. Loop 组件通过静态属性使用
public override Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
{
    var http = NetworkToolsAccessor.HttpClient;
    if (http == null) return Fail("网络服务未初始化");
    // ...
}
```

### 事件通知桥接

需要跨作用域触发事件时，用静态 `Action` 回调：

```csharp
internal static class ScheduledTasksNotifier
{
    public static Action? OnReschedule { get; set; }
    public static void NotifyChanged() => OnReschedule?.Invoke();
}

// Global 组件在 TimerLoop 中注册回调
ScheduledTasksNotifier.OnReschedule = () => _rescheduleSignal.Set();

// Loop 组件在任务变更后通知
ScheduledTasksNotifier.NotifyChanged();
```

### 要点

- Accessor 类放在 Global 和 Loop 组件都能引用的命名空间（通常是同一插件项目内）
- 属性用 `private set`，只能通过 `Configure()` 写入
- 可空类型 + null 检查，防止 Global 组件还没初始化时 Loop 组件就读取
- **参考实现**：`Plugin.NetworkTools/NetworkToolsAccessor.cs`、`Plugin.WebSearch/WebSearchAccessor.cs`、`Plugin.ScheduledTasks/ScheduledTasksNotifier.cs`

---

## 2. 异步通知 Drain 模式

### 问题

Global 组件的后台异步工作（下载完成、定时任务到期）完成后，怎么让 AI 在下一轮感知到？不能轮询（浪费 token），也不能直接调用 Loop 组件的方法（跨作用域）。

### 方案

Global 组件把通知写入**线程安全队列**，Loop 组件在 `OnBeforeInvokeAsync` 中 drain，在 `BuildPromptSection` 中注入。

```csharp
// === Global 侧：后台工作完成时入队 ===
internal class DownloadStore
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<DownloadNotification>>
        _queues = new();

    // 后台下载完成时调用
    public void EnqueueNotification(string loopId, DownloadNotification notification)
    {
        var queue = _queues.GetOrAdd(loopId, _ => new());
        queue.Enqueue(notification);
    }

    // Loop 侧 drain（一次性取出所有）
    public List<DownloadNotification> DrainNotifications(string loopId)
    {
        var results = new List<DownloadNotification>();
        if (_queues.TryGetValue(loopId, out var queue))
        {
            while (queue.TryDequeue(out var n)) results.Add(n);
        }
        return results;
    }
}

// === Loop 侧：每轮 AI 调用前 drain + 注入 prompt ===
public override Task OnBeforeInvokeAsync()
{
    var store = NetworkToolsAccessor.Store;
    if (store != null)
    {
        var notifications = store.DrainNotifications(_loopId);
        _pendingNotifications.AddRange(notifications);
    }
    return Task.CompletedTask;
}

public override string? BuildPromptSection()
{
    if (_pendingNotifications.Count == 0) return null;

    var lines = _pendingNotifications.Select(n => $"[下载完成] {n.FileName}");
    return string.Join("\n", lines);
}
```

### 要点

- **OnBeforeInvokeAsync** 做 drain（副作用），**BuildPromptSection** 做展示（纯读取）
- 队列按 loopId 隔离，每个 Loop 只消费自己的通知
- `ConcurrentQueue` + `TryDequeue` 保证线程安全（后台线程入队 + AI 轮次 drain）
- 通知是一次性的（drain 后清除），不会重复注入
- **参考实现**：`Plugin.NetworkTools/DownloadStore.cs` + `NetworkToolsLoopComponent.cs`、`Plugin.ScheduledTasks/ScheduledTasksTimerComponent.cs`

---

## 3. Global Timer + Loop Storage 分离

### 问题

多个 Loop 各自有定时任务，但每个 Loop 不能独立跑定时器（资源浪费 + 不精确）。怎么用一个全局定时器服务所有循环？

### 方案

**Global 组件**持有唯一定时器，扫描所有 Loop 的存储目录找到最早触发时间。**Loop 组件**只管自己的 JSON 文件。

```
Storage/PluginData/scheduled-tasks/
├── per-channel-abc123/
│   └── tasks.json          ← Loop 组件读写自己的文件
├── per-channel-def456/
│   └── tasks.json
└── _global/                 ← Global 组件扫描所有子目录
```

```csharp
// === Global Timer 组件 ===
private (DateTime? nextFire, string? loopId) FindEarliestTask()
{
    var pluginDataDir = Path.Combine(PathConfig.StoragePath, "PluginData", "scheduled-tasks");
    DateTime? earliest = null;
    string? earliestLoopId = null;

    foreach (var dir in Directory.GetDirectories(pluginDataDir))
    {
        var loopId = Path.GetFileName(dir);
        if (loopId == "_global") continue;

        var tasksFile = Path.Combine(dir, "tasks.json");
        if (!File.Exists(tasksFile)) continue;

        var tasks = LoadTasks(tasksFile);
        foreach (var task in tasks)
        {
            if (task.NextFireTime.HasValue && (!earliest.HasValue || task.NextFireTime < earliest))
            {
                earliest = task.NextFireTime;
                earliestLoopId = loopId;
            }
        }
    }

    return (earliest, earliestLoopId);
}

private async Task TimerLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        var (nextFire, loopId) = FindEarliestTask();
        if (!nextFire.HasValue) { await WaitForResignal(ct); continue; }

        var delay = nextFire.Value - DateTime.Now;
        if (delay > TimeSpan.Zero)
            await Task.WhenAny(Task.Delay(delay, ct), _rescheduleSignal.WaitAsync(ct));
        else
            _ctx.WakeLoop(loopId!);  // 到期，唤醒对应循环
    }
}
```

```csharp
// === Loop 组件：只管自己的 JSON ===
public override Task OnBeforeInvokeAsync()
{
    CheckDueTasks();  // 检查自己 JSON 中的到期任务
    return Task.CompletedTask;
}

public override string? BuildPromptSection()
{
    var text = Interlocked.Exchange(ref _pendingNotification, null);
    return text;  // 注入到期任务通知
}
```

### 要点

- **目录扫描代替中心化注册表**：每个 Loop 的 JSON 文件就是唯一真相源，增删任务不需要通知 Timer
- Timer 通过 `WakeLoop(loopId)` 精确唤醒目标循环，不是广播
- Loop 组件在 `OnBeforeInvokeAsync` 中做到期检查（防御性：即使 Timer 漏了也能补）
- `RecoverOverdueTasks` 启动时扫描，错过的任务立即触发
- **参考实现**：`Plugin.ScheduledTasks/ScheduledTasksTimerComponent.cs`（Global）+ `ScheduledTasksComponent.cs`（Loop）

---

## 4. FileToolBase 沙箱模式

### 问题

AI 可能通过文件工具尝试访问系统目录（`../../etc/passwd`）。怎么防止路径逃逸？

### 方案

所有文件操作继承 `FileToolBase` 基类，统一通过 `ResolvePath` 做沙箱校验。

```csharp
public abstract class FileToolBase : ITool
{
    protected string WorkspaceDir { get; }

    protected FileToolBase(IPluginStorage storage)
    {
        WorkspaceDir = storage.WorkspaceDirectory;
    }

    /// <summary>解析路径并校验沙箱。返回 null = 路径非法。</summary>
    protected string? ResolvePath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(WorkspaceDir, relativePath));
        var workspaceRoot = WorkspaceDir.EndsWith(Path.DirectorySeparatorChar)
            ? WorkspaceDir : WorkspaceDir + Path.DirectorySeparatorChar;

        return full.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)
            || full.Equals(WorkspaceDir, StringComparison.OrdinalIgnoreCase)
            ? full : null;
    }

    // 工具实现统一用 ResolvePath
    public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
    {
        var path = ResolvePath(inputs[0]);
        if (path == null) return Fail("路径超出工作区范围");
        // ...
    }
}
```

### 沙箱校验逻辑

1. `Path.Combine(WorkspaceDir, relativePath)` — 拼接工作区根 + 用户输入
2. `Path.GetFullPath()` — 规范化路径（解析 `..`、`.`、相对路径）
3. 检查规范化后的路径是否以 `WorkspaceDir/` 开头 — 防止逃逸
4. 额外允许精确等于 `WorkspaceDir` 本身（列目录场景）

### 共享工具方法

基类还提供常用工具方法，避免每个插件重复实现：

```csharp
protected static Task<ToolResult> Ok(string data) =>
    Task.FromResult(new ToolResult { Status = "success", Data = data });

protected static Task<ToolResult> Fail(string error) =>
    Task.FromResult(new ToolResult { Status = "failed", Error = error });

protected string TruncateWithSummary(string content, int maxChars)
{
    if (content.Length <= maxChars) return content;
    return content[..maxChars] + $"\n... (已截断，总长 {content.Length} 字符)";
}

protected string DetectFormat(string filePath)
{
    var ext = Path.GetExtension(filePath).ToLowerInvariant();
    return ext switch
    {
        ".json" => "json", ".xml" => "xml", ".csv" => "csv",
        ".png" or ".jpg" or ".jpeg" or ".gif" => "image",
        _ => "text"
    };
}
```

### 要点

- **所有**文件类插件（FileTools、FileOps、GroupFileTools）都继承这个基类
- 不要绕过 `ResolvePath` 直接用 `Path.Combine`
- `WorkspaceDirectory` 来自 `IPluginStorage`，不要硬编码路径
- 沙箱校验在 `GetFullPath` **之后**做，防止 `..` 绕过
- **参考实现**：`Plugins/FileToolKit.Shared/FileToolBase.cs`

---

## 其他模式速查

以下模式较简单或场景特定，了解即可，需要时再深入参考实现。

| 模式 | 何时需要 | 参考实现 |
|------|----------|----------|
| **双构造函数兼容**（IToolContext vs IPluginStorage） | 迁移旧工具到组件系统，需同时支持两种注册方式 | `Plugin.WorkingTools/PinboardTool.cs` |
| **Require\<T\> vs GetService\<T\>** | 决定服务缺失时该崩溃还是降级。基础设施用 Require，可选服务用 GetService | `Plugin.ReviewTools`（Require）/ `Plugin.MemoryTools`（GetService） |
| **TimeExpressionParser** | 需要解析用户自然语言时间表达式（"in 30m"、"每天 9 点"） | `Plugin.ScheduledTasks/TimeExpressionParser.cs` |
| **Skill 目录发现** | 需要零配置扩展机制（文件夹 + markdown frontmatter） | `Plugin.SkillTools/SkillEntry.cs` |
| **GetInputSchema 覆写** | 工具参数需要非 string 类型或详细 JSON Schema 描述 | `Plugin.CrossLoopTools/SendRequestTool.cs` |
| **Atomic File Save**（.tmp + Move） | 任何 JSON 持久化写入，防止崩溃导致数据损坏 | 所有插件的 `SaveXxx` 方法 |
| **BuildPromptSection 聚合** | 组件内多个工具各自贡献 prompt 片段 | `Plugin.WorkingTools/WorkingToolsComponent.cs` |
| **IDiceRegistry** | 插件想向骰子池注册随机事件 | `Plugin.MemoryTools/MemoryDiceFaces.cs` |
