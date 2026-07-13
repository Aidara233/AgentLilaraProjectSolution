# Session 类型系统重构实施方案

## 一、设计概述

### 1.1 三套字符串系统

| 概念 | 用途 | 取值 | 设置点 |
|------|------|------|--------|
| `AgentCore.EngineType` | ToolMeta.EngineTypes 工具过滤 | `"express"` / `"session"` / `"review"` | ExpressEngine 内 agentCore 实例设 "express"，RuntimeSession 设 "session"，ReviewEngine 已设 "review" |
| `ComponentHost._loopType` | LoopApplicability + ComponentConfig 组件过滤 | `"channel"` / `"task"` / `"review"` | ExpressEngine=channel，Channel session=channel，Task session=task，ReviewEngine=review |
| `SessionType` enum | SessionEngine Profile + 持久化 | Channel / Task | spec.json 持久化 |

**⚠ 二套字段不要混淆**：
- `ISubEngine.EngineType`：引擎类型标识（"Channel" / "Session" / "Timer" 等），用于 MasterEngine 查询引擎、WebUI 列表。本次只改 `ChannelEngine.SpawnCheck/RaidEngine.EngineType => "Express"` 一处（影响 MasterEngine.IsActive、EnginesProvider 等查询）。
- `AgentCore.EngineType`：实例属性（`public string? EngineType { get; set; }`），用于 ToolMeta.EngineTypes 过滤。本次 ChannelExpressCycle.cs:44 的 `"channel"` → `"express"`，RuntimeSession.cs:739 的 `"channel"` → `"session"`。
- 两者**命名相同但含义不同**，不要一次改动两个文件再用一处验证的行为去推断另一处。

### 1.2 ToolMeta.EngineTypes 默认语义翻转

- **原**：`null` = 全引擎可用（ToolRegistry.IsApplicableToEngine line 84）
- **新**：`null` = 仅 `"session"` 可用
- 需要 Express 或 Review 显式声明
- 实现点：`ToolRegistry.cs:80-86` 的 `IsApplicableToEngine`：

```csharp
public static bool IsApplicableToEngine(string toolName, string? engineType)
{
    if (string.IsNullOrEmpty(engineType)) return true;
    var meta = GetMeta(toolName);
    if (meta?.EngineTypes == null)
        return "session".Equals(engineType, StringComparison.OrdinalIgnoreCase);
    return meta.EngineTypes.Contains(engineType);
}
```

### 1.3 四层权限

```
finalComponents = ResolvedComponentIds                     // compact 路径直接用，跳过计算
  ?? (TypeProfile.DefaultComponentIds
      ∪ AdditionalComponents
      − ExcludedComponents)
  ∩ ComponentConfig.IsEnabled(name, typeKey)
```

`ComponentHost.InitAsync` 只接收并加载 finalComponents 列表内组件。当前 `InitAsync` 内部走 `ComponentRegistry.GetLoopComponents(_loopType)` 全量加载——变成由调用方传入 `IReadOnlyList<string>? finalComponentIds`，若为 null 才退回 GetLoopComponents 全量加载（向后兼容）。

### 1.4 SessionType

```csharp
public enum SessionType { Channel, Task }

public static class SessionTypeProfiles
{
    public static SessionTypeProfile Get(SessionType t) => t switch
    {
        SessionType.Channel => Channel,
        SessionType.Task => Task,
        _ => throw new ArgumentOutOfRangeException(nameof(t))
    };

    public static readonly SessionTypeProfile Channel = new()
    {
        Type = SessionType.Channel,
        DefaultCoreConfig = "ChannelCore",
        DefaultComponentIds = new[]
        {
            "basic-tools", "working-tools", "file-tools", "file-ops",
            "memory-tools", "system-tools", "cross-loop"
        },
        LoopApplicabilityKey = "channel"
    };

    public static readonly SessionTypeProfile Task = new()
    {
        Type = SessionType.Task,
        DefaultCoreConfig = "TaskCore",
        DefaultComponentIds = new[]
        {
            "basic-tools", "file-tools", "memory-tools", "cross-loop"
        },
        LoopApplicabilityKey = "task"
    };
}

public record SessionTypeProfile
{
    public SessionType Type { get; init; }
    public string DefaultCoreConfig { get; init; } = "";
    public string[] DefaultComponentIds { get; init; } = Array.Empty<string>();
    public string LoopApplicabilityKey { get; init; } = "";
    // 引擎类型字符串统一为 "session"——Express/Review 有独立的引擎类型，
    // 不与 SessionType 共用枚举
}
```

| | Channel | Task |
|---|---------|------|
| DefaultCoreConfig | ChannelCore | TaskCore |
| DefaultComponentIds | basic-tools, working-tools, file-tools, file-ops, memory-tools, system-tools, cross-loop | basic-tools, file-tools, memory-tools, cross-loop |
| PromptFiles | identity, style, social, arch, tech, channel_workflow | identity, style, arch, tech, task_workflow |
| LoopApplicabilityKey | `"channel"` | `"task"` |
| CanSpawnSubAgents | 是（system-ops 组件默认开） | 否（ComponentConfig `system-ops.task=false`） |

### 1.5 父子链条

- **SessionEngine 三张表**（替换 SubAgentAccessAdapter 自持的三张 dict）：
  - `ConcurrentDictionary<string, HashSet<string>> _activeChildren` — parentId → 子 sessionId 集合
  - `ConcurrentDictionary<string, string> _childToParent` — childId → 父 sessionId
  - `ConcurrentDictionary<string, string> _forwards` — 旧 sessionId → 新 sessionId（compact 迁移）

- **spec.json v2** 写入 `type` 字段（`SessionType.ToString()`）
- **state.json v2** 新增 `childSessionIds` / `effectiveParentId` / `forwardedTo`
- **end_session 阻塞**：模型调 end_session → Session 先 `SessionEngine.GetActiveChildren(selfId)` 判定是否有活跃子 → 有则拒绝退出 + 注入警告，无则正常结束
- **compact 不阻塞**：模型调 compact(summary) → `SessionEngine.CreateFromCompact` 创建新 session，自动迁移子链：
  - 遍历 `_activeChildren[oldId]`，把每个子的 `_childToParent` 改指 newId
  - 写 `_forwards[oldId] = newId`
  - `_activeChildren[newId] = _activeChildren[oldId]`；`_activeChildren.Remove(oldId)`
  - `_childToParent` 反向索引整体不动（key 是 childId，不需要迁移）
- **子完成时实时更新**：`OnChildSessionCompleted` → SessionEngine 锁内更新父 session 的 state.json（移除 childId），推送 dirty flag → 父下轮 BuildRoundInject 注入简化子概览："你有 N 个子 agent 正在运行"
- **report_to_parent 工具**：调用 `SessionEngine.ResolveEffectiveParent(childId)` 选最近活跃祖先（沿 `_forwards` 走），注入到祖先 session
- **list_sub_agents 工具已存在**：保持现状，靠 SessionEngine 查询
- **持久化恢复**：spec.json 恢复 `type`，state.json 恢复 `childSessionIds` / `effectiveParentId` / `forwardedTo`；`_activeChildren` + `_childToParent` 表在 RestoreAll 阶段从 state.json 反向重构（扫所有恢复的 session 的 childSessionIds 字段，建立索引；effectiveParentId 优先于 ParentSessionId 作为真实父指针）

### 1.6 清理

- 删 `ToolMetaAttribute.AllowSubAgent`（死属性，原默认 true，无可读取路径）
- 删 `ToolLevel.cs` 枚举（孤儿文件，无任何引用，已 grep 确认）
- 删 `Storage/Core/SessionCore.json` → 拆为 `ChannelCore.json` + `TaskCore.json`（两个独立文件）
- 删 `Storage/Core/Prompts/working_workflow.txt` → 拆为 `channel_workflow.txt` + `task_workflow.txt`
- 删 `"system"` 维度贯穿（LoopApplicability.System / ComponentConfig System / ComponentRegistry.SystemApplicability）
- 删 `LoopApplicabilityAttribute.SubAgent` 字段，改加 `Task`
- 死 session 不留档：`Storage/Sessions/` 直接清空，不做迁移（一次性 bootstrap）
- ChannelEngine → ExpressEngine 重命名（详见 Phase 6）

---

## 二、Phase 1：SDK 基础

### 新增文件

**`AgentLilara.PluginSDK/SessionType.cs`**
- 定义 `SessionType` 枚举
- 定义 `SessionTypeProfile` record
- 定义 `SessionTypeProfiles` 静态类（Channel / Task 两个预定义 Profile）
- 详见 §1.4 代码块

### 修改文件

**`AgentLilara.PluginSDK/CreateSessionSpec.cs`**

| 字段操作 | 原字段 | 新字段 |
|---------|--------|--------|
| 删 | `string[] ComponentIds` | — |
| 删 | `string CoreConfigName = "SessionCore"` | — |
| 加 | — | `SessionType Type { get; set; } = SessionType.Channel`（必填，默认 Channel 仅为兼容反序列化路径） |
| 加 | — | `string[]? ResolvedComponentIds { get; set; }`（compact 迁移用，非 null 则跳过 Profile 计算） |
| 加 | — | `string[]? AdditionalComponents { get; set; }` |
| 加 | — | `string[]? ExcludedComponents { get; set; }` |
| 加 | — | `int? ChannelId { get; set; }`（任何类型可指定频道，escalate 路径可省略，由 InitActions.connect_channel 接力） |
| 保留 | `AutoClaimChannel` / `InputPorts` / `OutputPorts` / `InitialState` / `InitActions` / `ParentSessionId` | 不变 |

**`AgentLilara.PluginSDK/IRuntimeSession.cs`**

加两个只读属性：
```csharp
string? EffectiveParentId { get; }      // 父 session 的有效 id（compact 链后可能跟 ParentSessionId 不同）
IReadOnlyList<string> ChildSessionIds { get; }
```

保留所有其他成员不变。`ParentSessionId` 继续代表"创建时的原始父"，区别于 `EffectiveParentId`。

**`AgentLilara.PluginSDK/ComponentAttribute.cs`**

`LoopApplicabilityAttribute` 类（line 14-21）：
- 删 `public Applicability System { get; set; }`
- 删 `public Applicability SubAgent { get; set; }`
- 加 `public Applicability Task { get; set; } = Applicability.Enabled;`
- 保留 `Channel` / `Review` 两个属性

`Applicability` 枚举（`Enabled` / `Disabled` / `NotApplicable`）不变。

**`AgentLilara.PluginSDK/ToolMetaAttribute.cs`**

- 删 `public bool AllowSubAgent { get; set; } = true;` 字段（line 18）
- 更新 `EngineTypes` 字段的 xml 注释：`null = 仅 session 可用；声明 ["express"] 或 ["review"] 才被对应引擎可见`

**`AgentLilara.PluginSDK/ToolLevel.cs`** — **删除文件**（孤儿）

### 受影响引用清理

- 所有引用 `ToolLevel` 的代码 → grep 确认无引用（已核查）
- 所有 `[ToolMeta(... AllowSubAgent = ...)]` → 全局移除该参数（共 12 处：CampusPrintComponent.cs × 11 + CompactTool.cs × 1）
- 所有 `[LoopApplicability(... SubAgent = ...)]` → 改为 `Task = ...`（共 3 处：ExternalDiceComponent.cs、GitComponent.cs、DicePoolComponent.cs）
- 所有 `[LoopApplicability(... System = ...)]` → 移除 `System` 具名参数（保留其他参数；如全部默认可不删属性）

### 验证
```bash
dotnet build AgentLilaraProjectSolution/AgentLilara.PluginSDK
```

---

## 三、Phase 2：持久化层

### 修改文件

**`AgentCoreProcessor/Engine/Session/SessionPersistence.cs`**

`SpecFormatVersion` 1 → 2
`StateFormatVersion` 1 → 2

**`SaveSpecAsync`**（line 42-61）：
- 字段 `componentIds = spec.ComponentIds` → **删**（spec 不再有此字段）
- 新增 `type = spec.Type.ToString()`
- 新增 `additionalComponents = spec.AdditionalComponents`
- 新增 `excludedComponents = spec.ExcludedComponents`
- 新增 `resolvedComponentIds = spec.ResolvedComponentIds`
- 新增 `channelId = spec.ChannelId`
- 删 `coreConfigName`（Type 决定）

**`SaveStateAsync`**（line 67-88）新增字段：
- `childSessionIds = state.ChildSessionIds ?? Array.Empty<string>()`
- `effectiveParentId = state.EffectiveParentId`
- `forwardedTo = state.ForwardedTo`

**`LoadSpec`**（line 134-155）：
- 删 `ComponentIds = wrapper.ComponentIds ?? Array.Empty<string>()`
- 加 `Type = Enum.TryParse<SessionType>(wrapper.Type, true, out var t) ? t : SessionType.Channel`（兼容旧版本，默认 Channel）
- 加 `AdditionalComponents = wrapper.AdditionalComponents`
- 加 `ExcludedComponents = wrapper.ExcludedComponents`
- 加 `ResolvedComponentIds = wrapper.ResolvedComponentIds`
- 加 `ChannelId = wrapper.ChannelId`
- 删 `CoreConfigName`（不再有此字段）

**`LoadState`**（line 157-228）：
- 加 `ChildSessionIds = wrapper.ChildSessionIds ?? Array.Empty<string>()`
- 加 `EffectiveParentId = wrapper.EffectiveParentId`
- 加 `ForwardedTo = wrapper.ForwardedTo`

**`SpecWrapper`**（line 287-300）：
- 删 `ComponentIds`
- 加 `[JsonProperty("type")] public string? Type;`
- 加 `[JsonProperty("additionalComponents")] public string[]? AdditionalComponents;`
- 加 `[JsonProperty("excludedComponents")] public string[]? ExcludedComponents;`
- 加 `[JsonProperty("resolvedComponentIds")] public string[]? ResolvedComponentIds;`
- 加 `[JsonProperty("channelId")] public int? ChannelId;`
- 删 `CoreConfigName`

**`StateWrapper`**（line 302-317）新增字段：
- `[JsonProperty("childSessionIds")] public string[]? ChildSessionIds;`
- `[JsonProperty("effectiveParentId")] public string? EffectiveParentId;`
- `[JsonProperty("forwardedTo")] public string? ForwardedTo;`

**`SessionStateSnapshot`**（line 272-284）新增字段：
- `public string[] ChildSessionIds { get; set; } = Array.Empty<string>();`
- `public string? EffectiveParentId { get; set; }`
- `public string? ForwardedTo { get; set; }`

**新增方法**（用于父子链维护）：
- `Task UpdateEffectiveParentAsync(string childId, string newParentId)` — 改写指定子 session 的 state.json 的 `effectiveParentId` 字段
- `Task AppendForwardAsync(string oldId, string newId)` — 写 `_forwards` 持久化（与 SessionEngine 内存表同步）

### 验证
```bash
dotnet build
```

---

## 四、Phase 3：组件过滤 + LoopApplicability + ComponentConfig 重命名

### 修改文件

**`AgentCoreProcessor/Component/ComponentConfig.cs`**

line 73：
```csharp
// 原
public static readonly string[] EngineTypes = ["channel", "system", "sub-agent", "review"];
// 新
public static readonly string[] EngineTypes = ["channel", "task", "review"];
```

`ComponentPermEntry`（line 90-115）：
- 删 `System` 属性
- `SubAgent` 重命名为 `Task`
- `IsEnabled` switch 删 `"system"` 分支；`"sub-agent"` → `"task"` 分支（用 Task 字段）
- `SetEnabled` switch 同上

**`AgentCoreProcessor/Component/ComponentRegistry.cs`**

`ComponentRegistration` record（line 9-17）：
- 删 `Applicability SystemApplicability`
- `SubAgentApplicability` → `TaskApplicability`

`Register`（line 40-48）删 `SystemApplicability`，`SubAgentApplicability: ...` → `TaskApplicability: loopAttr?.Task ?? Applicability.Enabled`

`GetLoopComponents`（line 66-81）switch：
- 删 `"system"` 分支
- `"sub-agent"` → `"task"`（映射到 `r.TaskApplicability`）

**`AgentCoreProcessor/Component/ComponentHost.cs`**

`InitAsync` 签名修改：
```csharp
// 原
public async Task InitAsync()
// 新
public async Task InitAsync(IReadOnlyList<string>? finalComponentIds = null)
```

- 若 `finalComponentIds != null` → 仅加载列表内组件（按 ComponentRegistry 注册顺序 + 名称匹配）
- 若为 null → 退回现有逻辑 `ComponentRegistry.GetLoopComponents(_loopType)` 全量加载（向后兼容）

`GlobalComponentHost.GetVisibleTools(string loopType)` switch 删 `"system"`，`"sub-agent"` → `"task"`

**`AgentCoreProcessor/Engine/Core/MasterEngine.cs`**

`ToggleComponentLiveAsync` 内部 switch 删 `"system"` 分支，`"sub-agent"` → `"task"`

### 插件 LoopApplicability 引用更新

逐个文件改 `SubAgent = X` → `Task = X`，删 `System = X`：

| 文件 | 修改 |
|------|------|
| Plugins/Plugin.ExternalDice/ExternalDiceComponent.cs:7 | `SubAgent = NotApplicable` → `Task = NotApplicable`，删 `Review` 不变 |
| Plugins/Plugin.GitTools/GitComponent.cs:15 | `System = Disabled, SubAgent = Enabled` → 删 System，`Task = Enabled` |
| Plugins/Plugin.GroupFileTools/GroupFileToolsComponent.cs:8 | 删 `System = Disabled` |
| Plugins/Plugin.WorkingTools/WorkingToolsComponent.cs:8 | 删 `System = Disabled` |
| Plugins/Plugin.CrossLoopTools/CrossLoopComponent.cs:7 | 删 `System = Enabled` |
| Plugins/Plugin.BasicTools/BasicToolsComponent.cs:8 | 删 `System = Disabled` |
| Plugins/Plugin.WebSearch/WebSearchLoopComponent.cs:6 | 删 `System` 参数 |
| Plugins/Plugin.NetworkTools/NetworkToolsLoopComponent.cs:8 | 删 `System` 参数 |
| Plugins/Plugin.NetworkTools/BrowserToolsLoopComponent.cs:7 | 删 `System` 参数 |
| Plugins/Plugin.SystemTools/SystemOpsComponent.cs:15,94 | 删 `System = Enabled` |
| Plugins/Plugin.QuickActions/QuickActionsComponent.cs:7 | 删 `System = Disabled` |
| Plugins/Plugin.ScheduledTasks/ScheduledTasksComponent.cs:11 | 删 `System = Disabled` |
| Plugins/Plugin.ReviewTools/ReviewToolsComponent.cs:7 | 删 `System = NotApplicable`（保留 Channel=NotApplicable） |
| Plugins/Plugin.SkillTools/SkillToolsComponent.cs:7 | 无 System 字段，无需改 |
| Plugins/Plugin.SshTools/SshLoopComponent.cs:8 | 删 `System` 参数 |
| Plugins/Plugin.DicePool/DicePoolComponent.cs:7 | `SubAgent = NotApplicable` → `Task = NotApplicable` |

### Storage/Engine/ComponentConfig.json 默认补齐

```json
{
  "ShutdownTimeoutMs": 30000,
  "Components": {
    "system-ops":         { "channel": true,  "task": false, "review": false },
    "sub-agent-runtime":  { "channel": false, "task": true,  "review": false },
    "working-tools":      { "channel": true,  "task": false, "review": false },
    "file-ops":           { "channel": true,  "task": false, "review": false },
    "email":              { "channel": true,  "task": true,  "review": true  },
    "campus-print":       { "channel": true,  "task": true,  "review": true  }
  }
}
```

### 验证
```bash
dotnet build
```

---

## 五、Phase 4：提示词

### 新增文件

**`Storage/Core/ChannelCore.json`** — 频道会话（Express + Channel session）共用
```json
{
  "provider": "claude",
  "model": "claude-3-5-sonnet-20241022",
  "temperature": 0.4,
  "maxTokens": 4096,
  "systemPrompt": "",
  "promptFiles": [
    "identity.txt", "style.txt", "social.txt",
    "architecture_overview.txt", "tech_rules.txt",
    "channel_workflow.txt"
  ]
}
```

**`Storage/Core/TaskCore.json`** — Task 会话专用
```json
{
  "provider": "claude",
  "model": "claude-3-5-sonnet-20241022",
  "temperature": 0.4,
  "maxTokens": 4096,
  "systemPrompt": "",
  "promptFiles": [
    "identity.txt", "style.txt",
    "architecture_overview.txt", "tech_rules.txt",
    "task_workflow.txt"
  ]
}
```

**`Storage/Core/Prompts/channel_workflow.txt`**
> 你当前在频道会话中，可以和用户实时对话。社交感知由 social 上下文提供。结束后用 end_session 退回轻量 Express 对话。

**`Storage/Core/Prompts/task_workflow.txt`**
> 你是任务执行者，没有实时用户交互。完成后用 end_session 结束。

### 删除文件
- `Storage/Core/SessionCore.json`
- `Storage/Core/Prompts/working_workflow.txt`

### Express 路径如何用 ChannelCore

**`ChannelEngine.cs:75`** 当前 `private readonly AgentCore agentCore = new();`（默认 SessionCore）→ 改为 `private readonly AgentCore agentCore = new("ChannelCore");`

### 验证
JSON 格式正确（启动核验）；编译不依赖这些文件

---

## 六、Phase 5：创建路径

### 修改文件

**`AgentCoreProcessor/Engine/Worker/ChannelExpressCycle.cs`** — escalate 路径

line 174-182 当前 CreateSessionSpec：
```csharp
sessionEngine.CreateSession(new CreateSessionSpec
{
    ComponentIds = new[] { "basic-tools", "working-tools", "file-tools", "file-ops", "memory-tools" },
    InitActions = new List<ToolCall> { ... },
    InitialState = $"用户请求切换到工作模式，原因：{reason}\n..."
});
```

改为：
```csharp
sessionEngine.CreateSession(new CreateSessionSpec
{
    Type = SessionType.Channel,                          // 频道会话（全功能）
    InitActions = new List<ToolCall> { ... },            // 不变
    InitialState = $"用户请求切换到工作模式，原因：{reason}\n..."
});
```

组件列表不再硬编码——由 SessionTypeProfiles.Channel.DefaultComponentIds 决定，ComponentConfig 二次过滤。

**`AgentCoreProcessor/Engine/Session/SessionEngine.cs`**

`CreateSession`（line 58-90）改造：
```csharp
public IRuntimeSession CreateSession(CreateSessionSpec spec)
{
    var profile = SessionTypeProfiles.Get(spec.Type);
    // 计算 finalComponents
    string[] finalComponents;
    if (spec.ResolvedComponentIds != null)
    {
        finalComponents = spec.ResolvedComponentIds;
    }
    else
    {
        var set = new HashSet<string>(profile.DefaultComponentIds, StringComparer.OrdinalIgnoreCase);
        if (spec.AdditionalComponents != null) set.UnionWith(spec.AdditionalComponents);
        if (spec.ExcludedComponents != null) set.ExceptWith(spec.ExcludedComponents);
        // ComponentConfig 二次过滤
        var cfg = ComponentConfig.Load();
        finalComponents = set
            .Where(c => cfg.IsEnabled(c, profile.LoopApplicabilityKey, defaultEnabled: true))
            .ToArray();
    }

    var session = new RuntimeSession(spec, finalComponents); // 重载见下
    // ...其余流程不变

    // 父子表登记（仅当 ParentSessionId 非空）
    if (!string.IsNullOrEmpty(spec.ParentSessionId))
    {
        lock (_childLinkLock)
        {
            _childToParent[session.SessionId] = spec.ParentSessionId;
            if (!_activeChildren.ContainsKey(spec.ParentSessionId))
                _activeChildren[spec.ParentSessionId] = new HashSet<string>();
            _activeChildren[spec.ParentSessionId].Add(session.SessionId);
        }
    }

    // AgentCore 用 Profile 决定的 CoreConfigName（而非 spec.CoreConfigName）
    var agentCore = new Core.AgentCore(profile.DefaultCoreConfig);
    // ... 其他不变
}
```

**`CreateFromCompact`**（line 205-243）改造：
```csharp
public IRuntimeSession CreateFromCompact(RuntimeSession oldSession, string summary)
{
    ReleaseSessionClaims(oldSession.SessionId);

    var profile = SessionTypeProfiles.Get(oldSession.Type);
    var oldSpec = new CreateSessionSpec
    {
        Type = oldSession.Type,
        ResolvedComponentIds = oldSession.GetComponentIdsForCompact(),  // 直接复用旧 session 组件
        AutoClaimChannel = true,
        InputPorts = new Dictionary<string, PortSpec>(StringComparer.OrdinalIgnoreCase),
        OutputPorts = new Dictionary<string, string>(),
        InitialState = $"[上一会话摘要]\n...\n\n请基于上述总结继续未完成的工作。",
        InitActions = new List<ToolCall>(oldSession.GetInitActionsForCompact()),
        ParentSessionId = oldSession.SessionId
    };

    var newSession = (RuntimeSession)CreateSession(oldSpec);
    newSession.InheritRoundCount(oldSession.GetRoundCountForCompact());

    // 子链迁移
    lock (_childLinkLock)
    {
        if (_activeChildren.TryRemove(oldSession.SessionId, out var childrenOfOld))
        {
            _activeChildren[newSession.SessionId] = childrenOfOld;
            foreach (var childId in childrenOfOld)
            {
                if (_childToParent.ContainsKey(childId))
                    _childToParent[childId] = newSession.SessionId;
                _persisterFor(childId)?.UpdateEffectiveParentAsync(childId, newSession.SessionId).Wait();
            }
        }
        _forwards[oldSession.SessionId] = newSession.SessionId;
    }

    Signal.Event(LogGroup.Engine, "compact 创建新 session",
        new { oldId = oldSession.SessionId, newId = newSession.SessionId, summaryLen = summary.Length,
              childrenMigrated = _activeChildren.TryGetValue(newSession.SessionId, out var c) ? c.Count : 0 });

    return newSession;
}
```

**新增字段**：
```csharp
private readonly ConcurrentDictionary<string, HashSet<string>> _activeChildren = new();
private readonly ConcurrentDictionary<string, string> _childToParent = new();
private readonly ConcurrentDictionary<string, string> _forwards = new();
private readonly object _childLinkLock = new();
```

**新增方法**：
```csharp
public IReadOnlyCollection<string>? GetActiveChildren(string sessionId)
{
    return _activeChildren.TryGetValue(sessionId, out var s) ? s : null;
}
public string? ResolveEffectiveParent(string childId)
{
    var current = childId;
    while (_childToParent.TryGetValue(current, out var p))
    {
        if (_forwards.TryGetValue(p, out var forwarded))
        {
            current = forwarded;
            continue;
        }
        return p;
    }
    return null;
}
public void OnChildCompleted(string childId)
{
    lock (_childLinkLock)
    {
        if (_childToParent.TryRemove(childId, out var parentId))
        {
            if (_activeChildren.TryGetValue(parentId, out var siblings))
            {
                siblings.Remove(childId);
                if (siblings.Count == 0) _activeChildren.Remove(parentId);
            }
            // 持久化层更新父 state.json（移除 childId from list）
            var parent = GetSession(parentId) as RuntimeSession;
            parent?.RemoveChildFromState(childId);
        }
    }
}
```

**ScheduledTask / TODO 创建路径**（line 446 / line 472）：

```csharp
// 原 ComponentIds = new[] { "basic-tools", "file-tools", "memory-tools" }
// 改为
Type = SessionType.Task
```

**RestoreAll**（line 499-）：
- 恢复时读 spec.Type（默认 Channel）
- 从 state.json 读 `childSessionIds` / `effectiveParentId` 反向重构 `_activeChildren` + `_childToParent`

**`AgentCoreProcessor/Engine/Session/RuntimeSession.cs`**

构造函数重载（line 90 / 99）：
```csharp
// 原
public RuntimeSession(CreateSessionSpec spec)
internal RuntimeSession(CreateSessionSpec spec, string fixedSessionId) : this(spec)

// 新（接受 finalComponents 跳过内部计算）
public RuntimeSession(CreateSessionSpec spec, string[]? finalComponents = null)
internal RuntimeSession(CreateSessionSpec spec, string fixedSessionId, string[]? finalComponents = null)
    : this(spec, finalComponents)
{
    SessionId = fixedSessionId;
}
```

字段与构造内修改：
```csharp
private readonly List<string> _componentIds = new();
// 原构造内 _componentIds.AddRange(spec.ComponentIds);
// 新
if (finalComponents != null) _componentIds.AddRange(finalComponents);
else throw new ArgumentException("finalComponents 由 SessionEngine 计算，不应为 null", nameof(finalComponents));
```

新增字段：
```csharp
public SessionType Type { get; }
public string? EffectiveParentId { get; private set; }
public IReadOnlyList<string> ChildSessionIds => _childSessionIds;
private readonly List<string> _childSessionIds = new();
public string? ForwardedTo { get; private set; }
```

`InitComponents`（line 119-134）：
```csharp
// 原 _componentLoopType = loopType == "session" ? "channel" : loopType;
// 新
_componentLoopType = SessionTypeProfiles.Get(_spec.Type).LoopApplicabilityKey;
```

不依赖 loopId 提取 loopType。Router 路径仍可保留 `ExtractLoopType(loopId)` 用于路由判断。

行 739：
```csharp
// 原 _agentCore!.EngineType = "channel";
// 新
_agentCore!.EngineType = "session";
```

**end_session 阻塞**：模型调 end_session 的处理逻辑（在工具副作用分支）加：
```csharp
case "end_session":
    var activeChildren = _sessionEngine?.GetActiveChildren(SessionId);
    if (activeChildren != null && activeChildren.Count > 0)
    {
        // 拒绝退出，注入警告
        InjectExternal($"[系统警告] 你还有 {activeChildren.Count} 个活跃子 agent，请先 stop_sub_agent 后再 end_session。");
        continueLoop = true;  // 继续循环
        break;
    }
    // 否则正常退出
    ...
```

**state snapshot**（`BuildStateSnapshot`）增加新字段：
```csharp
return new SessionStateSnapshot
{
    // 现有字段...
    ChildSessionIds = _childSessionIds.ToArray(),
    EffectiveParentId = EffectiveParentId,
    ForwardedTo = ForwardedTo
};
```

新增 internal 方法供 SessionEngine 调用：
```csharp
internal void RemoveChildFromState(string childId)
{
    lock (_childSessionIds) _childSessionIds.Remove(childId);
    _persister?.SaveStateAsync(BuildStateSnapshot()).Wait();
}
internal void AddChild(string childId) { lock (_childSessionIds) _childSessionIds.Add(childId); }
internal string[]? GetComponentIdsForCompact() => _componentIds.ToArray();
```

**`AgentCoreProcessor/Component/SubAgentAccessAdapter.cs`** 重构

删除字段（line 36-40）：
```csharp
private readonly ConcurrentDictionary<string, MailboxInputPort> _childMailboxes = new();
private readonly ConcurrentDictionary<string, string> _parentLoopIds = new();
private readonly ConcurrentDictionary<string, string> _loopIdToChildSession = new();
// 保留 _waitTcs（仍由 adapter 管的本地等待）
```

`Create`（line 59-113）改造：
```csharp
public SubAgentInfo Create(string instruction, SubAgentOptions? options = null)
{
    var se = GetSessionEngine();
    if (se == null) return new SubAgentInfo { SessionId = "", IsAlive = false };

    options ??= new SubAgentOptions();

    var spec = new CreateSessionSpec
    {
        Type = SessionType.Task,                                   // 子 agent 默认 Task
        InitialState = options.InitialState ?? instruction,
        ParentSessionId = options.ParentLoopId,                     // 改为传递纯 sessionId（不含 prefix）
        AdditionalComponents = options.ComponentIds,                // Task profile 默认 + 用户指定 additional
        InputPorts = new()                                          // 子 agent 无频道端口
    };

    var session = se.CreateSession(spec);
    var sessionId = session.SessionId;

    // 附加 mailbox
    var mailbox = new MailboxInputPort();
    if (session is RuntimeSession rsMailbox) rsMailbox.AddInputPort("mailbox", mailbox);
    _childMailboxes[sessionId] = mailbox;   // 仅保留 mailbox 通信，不再用此做反查表

    // 父子关系已由 SessionEngine.CreateSession 内部登记

    if (session is RuntimeSession rsComplete) rsComplete.OnCompleted += OnChildSessionCompleted;

    return new SubAgentInfo { SessionId = sessionId, IsAlive = session.IsAlive, ParentLoopId = options.ParentLoopId };
}
```

子 agent 不再需要 `sub-agent-runtime` 额外组件——Task profile 默认不含它，但 AdditionalComponents 可以追加。**需要给 Task profile 默认列表加上 sub-agent-runtime**，否则 report_to_parent 工具不存在。修正 SessionTypeProfiles.Task.DefaultComponentIds：
```csharp
public static readonly SessionTypeProfile Task = new()
{
    Type = SessionType.Task,
    DefaultCoreConfig = "TaskCore",
    DefaultComponentIds = new[]
    {
        "basic-tools", "file-tools", "memory-tools", "cross-loop",
        "sub-agent-runtime"  // 子 agent 回报工具默认开
    },
    LoopApplicabilityKey = "task"
};
```

`ReportToParent`（line 205-228）：
```csharp
public bool ReportToParent(string callerLoopId, string content, ReportState state)
{
    // callerLoopId = "session:xxx"，取冒号后段为 childId
    var childId = callerLoopId.StartsWith("session:") ? callerLoopId[8..] : callerLoopId;

    var effectiveParent = GetSessionEngine()?.ResolveEffectiveParent(childId);
    if (string.IsNullOrEmpty(effectiveParent))
    {
        Signal.Warn(LogGroup.Engine, "ReportToParent: 无有效父 session", new { childId });
        return false;
    }

    var parent = GetSessionEngine()?.GetSession(effectiveParent) as RuntimeSession;
    if (parent != null)
    {
        parent.InjectExternal($"[子会话 {childId} 回报] ({state}) {content}");
        Signal.Event(LogGroup.Engine, "子 agent 回报父会话",
            new { childId, parentId = effectiveParent, state = state.ToString(), contentLen = content.Length });
        return true;
    }

    // 父已退出（不在活动 sessions 中）→ 仅记日志，丢弃
    Signal.Warn(LogGroup.Engine, "ReportToParent: 父 session 不存在或非 RuntimeSession",
        new { childId, effectiveParent });
    return false;
}
```

`OnChildSessionCompleted`（line 258-296）改：
```csharp
private void OnChildSessionCompleted(IRuntimeSession session)
{
    var sessionId = session.SessionId;
    try
    {
        if (_childMailboxes.TryRemove(sessionId, out var mailbox)) mailbox.Complete();
        var se = GetSessionEngine();
        se?.OnChildCompleted(sessionId);   // 由 SessionEngine 统一删父的子集
        if (_waitTcs.TryRemove(sessionId, out var tcs)) tcs.TrySetResult(true);

        // 通知父 session
        var effectiveParent = se?.ResolveEffectiveParent(sessionId);
        if (!string.IsNullOrEmpty(effectiveParent))
        {
            var parent = se?.GetSession(effectiveParent) as RuntimeSession;
            var preview = (session as RuntimeSession)?.ExtractFinalOutput() ?? "";
            parent?.InjectExternal($"[子会话 {sessionId} 已完成] 状态：{session.Status}" +
                (string.IsNullOrEmpty(preview) ? "" : $"\n内容预览：{(preview.Length > 300 ? preview[..300] + "..." : preview)}"));
        }
    }
    catch (Exception ex) { Signal.Error(LogGroup.Engine, "OnChildSessionCompleted 异常", new { sessionId, error = ex.Message }); }
}
```

`List`（line 154-175）改：
```csharp
public List<SubAgentInfo> List(string? parentLoopId = null)
{
    var se = GetSessionEngine();
    if (se == null) return new();
    var parentId = parentLoopId?.StartsWith("session:") == true
        ? parentLoopId[8..]
        : parentLoopId;
    var children = parentId != null ? se.GetActiveChildren(parentId) : null;
    // 如果 parentLoopId 是 "channel:N"，需要 SessionEngine 反查 Full claimer 之后取其子
    if (parentId == null && parentLoopId != null && LoopId.IsChannel(parentLoopId, out var cid))
    {
        var claimer = se.GetFullClaimer(cid);
        if (!string.IsNullOrEmpty(claimer)) children = se.GetActiveChildren(claimer);
    }

    var list = new List<SubAgentInfo>();
    if (children == null) return list;
    foreach (var cid in children)
    {
        var s = se.GetSession(cid);
        if (s == null) continue;
        list.Add(ToInfo(s, cid));
    }
    return list;
}
```

**`AgentCoreProcessor/Tool/Core/CompactTool.cs`**

line 19：
```csharp
// 原
[ToolMeta(EngineTypes = new[] { "channel" }, ContinueLoop = false, AllowSubAgent = false, ...)]
// 新
[ToolMeta(EngineTypes = new[] { "session" }, ContinueLoop = false, ...)]
```
删 `AllowSubAgent = false` 参数（Phase 1 字段已删）

### 验证
```bash
dotnet build
```

---

## 七、Phase 6：ChannelEngine → ExpressEngine 重命名 + tool EngineTypes 更新 + WebUI

### 7a. 引擎重命名

#### 重命名文件
- `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs` → `ExpressEngine.cs`
- `AgentCoreProcessor/Engine/Worker/ChannelEngineSpawnCheck.cs` → `ExpressEngineSpawnCheck.cs`
- `AgentCoreProcessor/Engine/Worker/ChannelExpressCycle.cs` → `ExpressCycle.cs`

#### 类名 / 字段 / 变量重命名

涉及 ~6 处类声明 + ~31 处变量引用：
- `class ChannelEngine` → `class ExpressEngine`
- `class ChannelEngineSpawnCheck` → `class ExpressEngineSpawnCheck`
- 类内字段 / 方法名（如 `_lastSessionContext` 等 ChannelEngine-only 命名可保留不改，因为不影响功能）

**`ISubEngine.EngineType` 返回值**（这决定 MasterEngine.IsActive / EnginesProvider / WebUI）：
- `ChannelEngine.cs:32` `public string EngineType => "Channel";` → `ExpressEngine.cs` `public string EngineType => "Express";`
- `ChannelEngineSpawnCheck.cs:17` `public string EngineType => "Channel";` → `ExpressEngineSpawnCheck.cs` `public string EngineType => "Express";`

**`MasterEngine.cs:144` SpawnCheck 注册 key `"Channel"` → `"Express"`**

`MasterEngine.RegisterSpawnCheck` 内部用 `spawnChecks.FirstOrDefault(c => c.EngineType == type)` 检索 SpawnCheck。`EngineType` 字符串只要和 SpawnCheck 类的返回值一致即可。需要更新：
- `MasterEngine.cs` 中所有查询 `"Channel"` 的位置 → `"Express"`（grep 同步核查）

`CallerTag` 字符串：
- `ChannelEngine.cs:159, 209` 当前 `agentCore.CallerTag = $"Channel:{channelId}";` → `$"Express:{channelId}";`
- ModelCallLog 中现存 `Channel:xxx` 历史记录不影响功能，仅显示差异
- Token 统计 WebUI 显示的 CallerTag 也变为 "Express:xxx"，等 Phase 6 后续显示侧改文案

**保留不变**：
- `LoopId.ForChannel(channelId)` 仍然返回 `"channel:{channelId}"` 字符串格式（loopId 是路由用，不是引擎名）
- Signal scope 字符串 `$"channel:{channelId}"` 保留
- 加入频道时的 `connect_channel` 工具名保留
- `ChannelConfig` 类名（与频道本身相关，不与引擎名耦合）保留
- 所有 `channel:{id}` / `ChannelId` 字段继续保留——这是频道概念，不与引擎名重合

#### WebUI 更新

```
EnginesProvider.cs:150  "Channel" → "Express"  
EngineDetailProvider.cs   "channel_{id}" 路由 → "express_{id}"  
                          ChannelEngineStatusSource → ExpressEngineStatusSource
DashboardProvider.cs      ChannelEngine → ExpressEngine 引用更新
ChannelProvider.cs        ChannelEngine → ExpressEngine 引用更新（如有）
ConsoleProvider.cs        ChannelEngine → ExpressEngine 引用更新（如有）
```

#### 注释更新

所有 `// ChannelEngine ...` / `/// ChannelEngine ...` 注释改为 ExpressEngine（~25 处）
保留所有 "channel loopType" / "频道" 相关注释——loopType 字符串仍是 "channel"，不要混改

### 7b. ToolMeta.EngineTypes 默认语义翻转

**`AgentCoreProcessor/Tool/ToolRegistry.cs`** line 80-86：

```csharp
public static bool IsApplicableToEngine(string toolName, string? engineType)
{
    if (string.IsNullOrEmpty(engineType)) return true;
    var meta = GetMeta(toolName);
    if (meta?.EngineTypes == null)
        return "session".Equals(engineType, StringComparison.OrdinalIgnoreCase);
    return meta.EngineTypes.Contains(engineType);
}
```

### 7c. 核心工具 EngineTypes 更新

| 工具 (文件) | 原 EngineTypes | 新 EngineTypes |
|------|---------------|---------------|
| `EscalateTool.cs:9` | `["channel"]` | `["express"]` |
| `CoreTools.cs:30` (wait) | `["channel", "system"]` | `["express", "session"]` |
| `CoreTools.cs` (continue_loop) | null（不声明） | `["express", "session"]`（按需显式） |
| `CoreTools.cs` (end_session) | null | `["session"]`（按需显式） |
| `CompactTool.cs:19` | `["channel"]`（Phase 5 已改 `["session"]`） | 已处理 |
| `ConnectChannelTool.cs:13/38/62` | `["channel"]` | `["session"]` |
| `DisableComponentTool.cs:9` | `["channel"]` | `["session"]` |
| `EnableComponentTool.cs:9` | `["channel"]` | `["session"]` |
| `RefineImageTool.cs:9` | `["channel"]` | `["session"]` |
| `GetImageTextTool.cs:11` | `["channel"]` | `["session"]` |

### 7d. 插件工具 EngineTypes 声明

**Express 应可见（声明 `["express", "session"]`）**：

| 插件 | 工具 |
|------|------|
| Plugin.BasicTools | speak, send_media, send_file, thinking, adapter_action |
| Plugin.MemoryTools | memory_store, memory_get, memory_search, memory_list, memory_stats, memory_link_get |
| Plugin.FileTools | read_text, list_dir |
| Plugin.FileOps | archive_list, search_files, grep_files, file_info, file_hash, compare_files |
| Plugin.ImageTools | read_image, get_ocr |

**Session 默认（不声明，null → session）**：所有其他插件工具保持不声明 EngineTypes

**Review 专属（声明 `["review"]`）**：Plugin.ReviewTools 全部 12 个 review_* 工具加 `EngineTypes = new[] { "review" }`

### 7e. ChannelExpressCycle.cs（已重命名为 ExpressCycle.cs）

line 44 `agentCore.EngineType = "channel";` → `agentCore.EngineType = "express";`

line 52 `agentCore.GlobalComponentTools = ctx.GlobalComponentHost?.GetVisibleTools("channel").ToList();`
- 这里 `"channel"` 是 loopType（ComponentHost._loopType），不是 AgentCore.EngineType
- ExpressEngine 的 loopType 也应是 `"channel"`（与频道绑定的 LoopApplicability.Channel），所以**这行不改**

line 55 `ToolRegistry.GetExpressToolDefinitions("channel");` → `ToolRegistry.GetExpressToolDefinitions("express");`（因为 `engineType` 字段已变 `"express"`）

### 验证
```bash
dotnet build
```

---

## 八、Phase 7：清理 + WebUI 配置

### 清理

- 删 `ToolMetaAttribute.AllowSubAgent` 字段（Phase 1 已处理）
- 删 `ToolLevel.cs` 文件（Phase 1 已处理）
- 所有 `[ToolMeta(... AllowSubAgent = false)]` 移除该参数：
  - CampusPrintComponent.cs × 11 处（line 149/171/213/265/332/383/421/443/469/499/525）
  - CompactTool.cs × 1 处（Phase 5 已处理）
- 所有 `[LoopApplicability(... System = ...)]` 移除 `System` 字段（Phase 3 已处理）
- 所有 `[LoopApplicability(... SubAgent = ...)]` 改为 `Task = ...`（Phase 3 已处理）

### WebUI 文案

**`/p/plugins/permissions`**：
- "sub-agent" 列名 → "task"
- 删 "system" 列

**`/p/sessions`**：
- 加 `Type` 列 + `Active Child Sessions` 列

**`/p/engines`**：
- 所有 `EngineType == "Channel"` 查询 → `"Express"`（EnginesProvider.cs 同时改）

### 验证
```bash
dotnet build
```

---

## 九、Phase 8：文档 + 清空 Sessions + 编译测试

### 文档更新

- `CLAUDE.md` — 引擎名 / 会话类型架构更新（ChannelEngine → ExpressEngine、SessionType 系统、四层权限）
- `docs/architecture-map.md` — 会话层架构图重画（添加 SessionType + 父子链）
- `docs/plugins/README.md` — LoopApplicability 字段名 / ComponentConfig key 更新
- `docs/plugins/claude-quick-ref.md` — 同上 + 示例更新
- `docs/plugins/api-reference.md` — `LoopApplicabilityAttribute` 字段名
- `docs/core/agent-core.md` — EngineType 分类更新
- `docs/signal/signal-*.md` — ChannelEngine → ExpressEngine 更新
- 所有其他引用 "ChannelEngine" 的 docs 文件 grep 后批量更新

### 清空 Sessions

```powershell
Remove-Item -Recurse -Force Storage/Sessions/*
```

或在 `SessionEngine.RestoreAll` 中检测旧版本 spec（formatVersion < 2）时跳过 + 写入 Completed 标记。**推荐直接清空目录**，因为这是预生产阶段的 bootstrap。

### 编译 + 试运行

```powershell
cmd /c "taskkill /IM AgentCoreProcessor.exe /T /F"
dotnet build
dotnet run --test
```

### 试运行验证项

- [ ] Express 工具表只剩 5 个声明为 express 的工具（escalate + speak/send_media/memory_*）
- [ ] Express escalation 触发后，新 Channel session 用 ChannelCore.json 提示词
- [ ] Channel session 可见 system-ops 组件 + create_sub_agent 工具
- [ ] Task session 不可见 working-tools / system-ops（按 ComponentConfig 默认禁用）
- [ ] Task session 默认可见 sub-agent-runtime（report_to_parent 工具可调）
- [ ] `compact(summary)` 创建新 session，agent 链 childSessionIds 转移到新父
- [ ] `end_session` 在有活跃子时拒绝 + 注入警告
- [ ] `list_sub_agents` 返回子列表（SubAgentAccessAdapter.List 走 SessionEngine 查询）
- [ ] WebUI `/p/engines` 显示 "Express" 而非 "Channel"
- [ ] WebUI `/p/sessions` 显示 Type 列
- [ ] WebUI `/p/plugins/permissions` 显示 `channel` / `task` / `review` 三列
- [ ] CallerTag 显示 "Express:xxx"
- [ ] 重启后已 Running/Crashed session 全部恢复，`_activeChildren` / `_childToParent` 表重建

---

## 十、执行顺序总览

| Phase | 内容 | 改动量 |
|-------|------|--------|
| 1 | SDK 基础（SessionType + CreateSessionSpec + LoopApplicabilityTask 重命名 + 删 System/SubAgent 字段、ToolLevel）| 6 文件改/删 |
| 2 | 持久化层（schema v2 + 新字段读写 + UpdateEffectiveParent/AppendForward 方法）| 1 文件 |
| 3 | 组件过滤 + LoopApplicability 重命名 + 补 ComponentConfig 默认 + ~16 个插件文件改属性 | ~20 文件 |
| 4 | 提示词（ChannelCore/TaskCore JSON × 2 + workflow txt × 2 + ExpressEngine 改用 ChannelCore） | 2 增 + 2 删 + 1 改 |
| 5 | 创建路径（escalate/sub-agent/ScheduledTask/TODO/compact 五条 + SessionEngine 三张表 + RuntimeSession end_session 阻塞 + SubAgentAccessAdapter 重构） | ~6 文件大改 |
| 6 | ChannelEngine → ExpressEngine 重命名 + ToolMeta EngineTypes 默认翻转 + 152 个工具 EngineTypes 按矩阵更新 + WebUI 文案 | ~30 文件 |
| 7 | 清理 + WebUI + ComponentConfig | ~8 文件 |
| 8 | 文档 + 清空 Sessions + 编译测试 | ~15 文件 |

总计约 ~80 个改动点（不含工具 EngineTypes 单条声明批量）。