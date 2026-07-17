# 异步唤醒链路修复与统一 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复下载/SSH/日程三类异步唤醒功能的失效问题，并把 session-scoped 子资源（子 agent、下载、SSH）的"压缩迁移 + 父链追溯"统一到 SessionEngine 的单一抽象上。

**Architecture:** 引入 `ISessionResource` 接口让 session-scoped 子资源（子 agent / 下载 / SSH）在 SessionEngine 注册自己；SessionEngine 负责跨 compact 追溯活跃 session（`ResolveActiveSession`）并在 compact 时通知所有挂载资源 rebind。`MasterEngine.WakeLoop` 的 channel 路径改为先查 Session 占用，让 channel-scoped 的日程在 Session 活跃时也能送达。RuntimeSession 唤醒后排空组件通知才能进推理。下载/SSH 组件的 Channel applicability 改 Disabled 强制走 Session；ScheduleTaskTool 增加 `requires_work` 参数区分提醒类型，定时器按类型路由。

**Tech Stack:** .NET 8 / C# / xUnit / Newtonsoft.Json / AgentLilara Plugin SDK / SQLite-net-pcl

## Global Constraints

- 编译走 solution 全量：`dotnet build AgentLilaraProject.sln`（不能只 build 单项目，`$(SolutionDir)` 依赖）
- 杀进程用 `cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null`（Git Bash 斜杠参数陷阱）
- 插件改完要 `dotnet build`（触发 `CopyToHostPlugins` MSBuild 目标）
- 注释、commit message 英文；类名/方法名/变量名英文；其他文档/讨论中文
- 不强制 stop：Session 仅由模型调 `end_session` / 外部 `RequestStop` / CTS 取消终止
- Storage 不在 solution 目录下，配置文件在 `Storage/` 目录
- 数据库改动需 `DbManager.InitAsync` 加 `CreateTableAsync`
- 改动完成后按 CLAUDE.md 工作流走：build → commit → --test 试运行 → 文档

## 名词约定

- **session-scoped 子资源**：归属某个 session、需要在 session compact 时迁移到新 session 的资源。目前指：子 agent、下载、SSH 任务。
- **channel-scoped 资源**：归属某个频道的资源，与"当前谁处理这个频道"无关。目前指：日程提醒。
- **loopId**：字符串 ID，格式 `channel:N` / `session:GUID` / `task:GUID` / `review:xxx` / `system`。
- **effective parent / active session**：session 经过 0 或多次 compact 转发后最终落到的那一个还活着的 session。

---

## 文件结构总览

### 新增文件

| 文件 | 职责 |
|------|------|
| `AgentLilara.PluginSDK/Services/ISessionResourceAccess.cs` | SDK 暴露的子资源注册表接口，供插件 GlobalComponent 注册自己的资源 |
| `AgentCoreProcessor/Engine/Session/SessionResourceRegistry.cs` | SessionEngine 内的注册表实现，承载 `ISessionResource` 实例 + rebind/forward 链 |
| `AgentCoreProcessor.Tests/SessionResourceRegistryTests.cs` | 注册表的跨 compact 转发、rebind、孤儿清理的单元测试 |
| `AgentCoreProcessor.Tests/WakeLoopRoutingTests.cs` | MasterEngine.WakeLoop channel 路由的单元测试 |
| `AgentCoreProcessor.Tests/RuntimeSessionNotificationDrainTests.cs` | RuntimeSession 唤醒后先排空通知的集成测试 |

### 修改文件（按改动幅度排序）

| 文件 | 改什么 |
|------|--------|
| `AgentCoreProcessor/Engine/Session/SessionEngine.cs` | 新增 `_sessionResources` 注册表 / `ResolveActiveSession` / `RegisterSessionResource` / `UnregisterSessionResource` / 改造 `WakeUpSession` 走 forward / 改造 `CreateFromCompact` 触发 rebind / 改造 `StopSession`/`ForceKill` 通知资源 detach |
| `AgentCoreProcessor/Engine/Core/MasterEngine.cs` | `WakeLoop` channel 路径加 Session 占用优先检查 |
| `AgentCoreProcessor/Engine/Session/RuntimeSession.cs` | `WaitForActivationAsync` 返回后先排空 `OnBeforeInvokeAsync`+`BuildPromptSections`；`ApplyToolEffectAsync` end_session 分支增查 in-flight 子资源 |
| `AgentLilara.PluginSDK/ILoopComponent.cs` | 新增 `Task<bool> OnCanExitAsync()` 方法 |
| `AgentLilara.PluginSDK/ComponentBase.cs` | `LoopComponentBase.OnCanExitAsync` 默认实现返回 true |
| `AgentCoreProcessor/Component/ComponentHost.cs` | 新增 `QueryCanExitAsync()` 聚合所有 LoopComponent 的 OnCanExitAsync |
| `AgentCoreProcessor/Component/SubAgentAccessAdapter.cs` | 实装 `ISessionResource`，通过 SessionEngine 注册表统一替代私有 `_childToParent` 路径 |
| `Plugins/Plugin.NetworkTools/DownloadTask.cs` | `LoopId` 仍是字符串，但语义改为 session-scoped loopId（`session:GUID` 或 `channel:N` 仅当 Express 起下载——后者被禁用后不再出现） |
| `Plugins/Plugin.NetworkTools/DownloadStore.cs` | 实装 `ISessionResource`（在 GlobalComponent 初始化时注册到 SessionEngine），rebind 时把所有 `LoopId == oldId` 的 task 改成 `newId`；新增 `HasActiveTasks(loopId)` 供 sticky 查询 |
| `Plugins/Plugin.NetworkTools/NetworkToolsGlobalComponent.cs` | 初始化时通过 `ISessionResourceAccess` 注册 `DownloadStore` |
| `Plugins/Plugin.NetworkTools/NetworkToolsLoopComponent.cs` | `LoopApplicability` Channel 改 Disabled；实装 `OnCanExitAsync` 查 store active count |
| `Plugins/Plugin.SshTools/SshTask.cs` | `LoopId` 语义同上 |
| `Plugins/Plugin.SshTools/SshGlobalComponent.cs` | 实装 `ISessionResource` 注册；rebind 时改 `_runningTasks`/`_completedTasks` 的 LoopId；新增 `HasActiveTasks(loopId)` |
| `Plugins/Plugin.SshTools/SshLoopComponent.cs` | `LoopApplicability` Channel 改 Disabled；实装 `OnCanExitAsync` |
| `Plugins/Plugin.ScheduledTasks/ScheduleTaskTool.cs` | 新增 `requires_work` 布尔参数 |
| `Plugins/Plugin.ScheduledTasks/ScheduledTaskStore.cs` | `ScheduledTaskEntry` 新增 `RequiresWork` 字段；`AddTask` 接收参数 |
| `Plugins/Plugin.ScheduledTasks/ScheduledTasksTimerComponent.cs` | `FindEarliestTask` 返回 `(DateTime?, loopId, requiresWork)`；TimerLoop 按类型路由（有 session → wake session / 无 session + 需工作 → 冷启动 channel session / 无 session + 提一嘴 → wake Express） |
| `Plugins/Plugin.ScheduledTasks/ScheduledTasksComponent.cs` | 提醒文本里体现 `requires_work`（让模型决定是否走 escalate） |

---

## 任务依赖图

```
T1 (SDK 接口定义)
    ↓
T2 (SessionResourceRegistry + ResolveActiveSession + WakeUpSession forward)
    ↓
T3 (MasterEngine.WakeLoop channel 路由)    ← 可与 T4 并行
T4 (RuntimeSession 唤醒后排空通知)
    ↓
T5 (CreateFromCompact 触发 rebind + StopSession/ForceKill 通知 detach)
    ↓
T6 (SubAgentAccessAdapter 实装 ISessionResource)    ← 可与 T7/T8 并行
T7 (NetworkTools 改 Disabled + 实装 ISessionResource + sticky)
T8 (SshTools 改 Disabled + 实装 ISessionResource + sticky)
    ↓
T9 (ScheduleTaskTool 加 requires_work + 定时器按类型路由)
    ↓
T10 (ILoopComponent.OnCanExitAsync + ComponentHost.QueryCanExitAsync + RuntimeSession end_session 拦截)
    ↓
T11 (端到端 build + 手工 --test 验证 + 文档)
```

---

### Task 1: 定义 ISessionResource / ISessionResourceAccess SDK 接口

**Files:**
- Create: `AgentLilaraProjectSolution/AgentLilara.PluginSDK/Services/ISessionResourceAccess.cs`

**Interfaces:**
- Produces: `ISessionResource` 接口（`OnSessionCompactingAsync`、`OnSessionFreedAsync`、`HasActiveResources`）、`ISessionResourceAccess` 接口（`Register`、`Unregister`、`ResolveActiveSession`）—— 后续 T2/T6/T7/T8 全部依赖这两个接口

- [ ] **Step 1: 创建文件 `AgentLilara.PluginSDK/Services/ISessionResourceAccess.cs`**

```csharp
// AgentLilara.PluginSDK/Services/ISessionResourceAccess.cs
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK.Services
{
    /// <summary>
    /// session-scoped 子资源接口：资源归属某个 session，session compact 时被通知 rebind，
    /// session 死亡时被通知清理。实装者：SubAgentAccessAdapter、DownloadStore、SshGlobalComponent。
    /// </summary>
    public interface ISessionResource
    {
        /// <summary>所属 session 即将 compact 到 newSessionId。资源应改自己的 loopId 到 newSessionId。</summary>
        Task OnSessionCompactingAsync(string oldSessionId, string newSessionId);

        /// <summary>所属 session 已结束 / 被 ForceKill。资源应清理挂在该 session 上的状态。</summary>
        Task OnSessionFreedAsync(string sessionId);

        /// <summary>是否有挂在该 sessionId 上仍未完成的资源。用于 end_session 阻塞判定。</summary>
        bool HasActiveResources(string sessionId);
    }

    /// <summary>
    /// SessionEngine 暴露的子资源注册表。供插件 GlobalComponent 注册自己的 ISessionResource。
    /// </summary>
    public interface ISessionResourceAccess
    {
        /// <summary>注册一个 session-scoped 子资源。资源在 session compact/死亡时被回调。</summary>
        void Register(ISessionResource resource);

        /// <summary>反注册（资源自身关闭时调用）。</summary>
        void Unregister(ISessionResource resource);

        /// <summary>
        /// 跨 0 或多次 compact 转发，解析 loopId 当前对应的活跃 session ID。
        /// 若 loopId 本身不是 session（如 "channel:N"），原样返回。
        /// 若 loopId 是已死的 session（不在 _sessions 且不在 _forwards），返回 null。
        /// </summary>
        string? ResolveActiveSession(string loopId);
    }
}
```

- [ ] **Step 2: 编译 SDK 确认无语法错误**

Run: `dotnet build AgentLilaraProjectSolution/AgentLilara.PluginSDK/AgentLilara.PluginSDK.csproj`
Expected: PASS（SDK 不依赖其他项目）

- [ ] **Step 3: Commit**

```bash
cd AgentLilaraProjectSolution
git add AgentLilara.PluginSDK/Services/ISessionResourceAccess.cs
git commit -m "feat: add ISessionResource/ISessionResourceAccess SDK contracts"
```

---

### Task 2: SessionEngine 实装注册表 + ResolveActiveSession + WakeUpSession forward

**Files:**
- Create: `AgentLilaraProjectSolution/AgentCoreProcessor/Engine/Session/SessionResourceRegistry.cs`
- Modify: `AgentLilaraProjectSolution/AgentCoreProcessor/Engine/Session/SessionEngine.cs:34-50`（新增字段）、`:317-330`（替换 `ResolveEffectiveParent`）、`:420-425`（改造 `WakeUpSession`）
- Test: `AgentLilaraProjectSolution/AgentCoreProcessor.Tests/SessionResourceRegistryTests.cs`

**Interfaces:**
- Consumes: `ISessionResource` / `ISessionResourceAccess` from Task 1
- Produces: `SessionEngine.RegisterSessionResource` / `UnregisterSessionResource` / `ResolveActiveSession` / 改造后的 `WakeUpSession(sessionId)`（内部走 forward）—— T3/T5/T6/T7/T8 依赖

- [ ] **Step 1: 写失败测试 `SessionResourceRegistryTests.cs`**

```csharp
// AgentCoreProcessor.Tests/SessionResourceRegistryTests.cs
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK.Services;
using Xunit;

namespace AgentCoreProcessor.Tests;

public class SessionResourceRegistryTests
{
    [Fact]
    public async Task Register_then_ResolveActiveSession_returns_same_id_when_alive()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);
        var resource = new FakeResource();
        engine.RegisterSessionResource(resource);

        var resolved = engine.ResolveActiveSession("session:S1");
        Assert.Equal("session:S1", resolved);
    }

    [Fact]
    public async Task ResolveActiveSession_follows_forward_chain()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S2", alive: true);
        engine.SeedForward("S1", "S2"); // S1 compacted to S2
        var resolved = engine.ResolveActiveSession("session:S1");
        Assert.Equal("session:S2", resolved);
    }

    [Fact]
    public async Task ResolveActiveSession_returns_null_when_session_dead_and_not_forwarded()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("SX", alive: false);
        var resolved = engine.ResolveActiveSession("session:SX");
        Assert.Null(resolved);
    }

    [Fact]
    public async Task ResolveActiveSession_returns_non_session_loopId_unchanged()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);
        var resolved = engine.ResolveActiveSession("channel:5");
        Assert.Equal("channel:5", resolved);
    }

    [Fact]
    public async Task WakeUpSession_follows_forward_to_active_session()
    {
        var engine = TestHost.BuildSessionEngineWithFakeSession("S2", alive: true);
        engine.SeedForward("S1", "S2");
        var resource = new FakeResource();
        engine.RegisterSessionResource(resource);

        engine.WakeUpSession("S1"); // should wake S2
        Assert.True(engine.WasWoken("S2"));
    }

    private class FakeResource : ISessionResource
    {
        public string? LastRebindOld;
        public string? LastRebindNew;
        public string? LastFreed;
        public Task OnSessionCompactingAsync(string oldSessionId, string newSessionId)
        { LastRebindOld = oldSessionId; LastRebindNew = newSessionId; return Task.CompletedTask; }
        public Task OnSessionFreedAsync(string sessionId)
        { LastFreed = sessionId; return Task.CompletedTask; }
        public bool HasActiveResources(string sessionId) => false;
    }
}
```

注意：`TestHost.BuildSessionEngineWithFakeSession` / `SeedForward` / `WasWoken` 是测试辅助，需在 T2 Step 4 中创建。

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AgentLilaraProjectSolution/AgentCoreProcessor.Tests --filter SessionResourceRegistry`
Expected: FAIL with "RegisterSessionResource not found" / "TestHost not found"

- [ ] **Step 3: 创建 `SessionResourceRegistry.cs`（单元内部使用，嵌入 SessionEngine 即可）**

```csharp
// AgentCoreProcessor/Engine/Session/SessionResourceRegistry.cs
using System.Collections.Concurrent;
using System.Collections.Generic;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// SessionEngine 内部持有的 session-scoped 子资源注册表。
/// 负责在 compact / 释放 / 死亡时回调所有 ISessionResource。
/// </summary>
internal class SessionResourceRegistry
{
    private readonly ConcurrentDictionary<ISessionResource, byte> _resources = new();

    public void Register(ISessionResource r) => _resources.TryAdd(r, 0);
    public void Unregister(ISessionResource r) => _resources.TryRemove(r, out _);

    public async Task NotifyCompactingAsync(string oldSessionId, string newSessionId)
    {
        foreach (var r in _resources.Keys)
        {
            try { await r.OnSessionCompactingAsync(oldSessionId, newSessionId); }
            catch (System.Exception ex) { Logging.Signal.Warn(Logging.LogGroup.Engine, "ISessionResource OnSessionCompacting 异常", new { resource = r.GetType().Name, error = ex.Message }); }
        }
    }

    public async Task NotifyFreedAsync(string sessionId)
    {
        foreach (var r in _resources.Keys)
        {
            try { await r.OnSessionFreedAsync(sessionId); }
            catch (System.Exception ex) { Logging.Signal.Warn(Logging.LogGroup.Engine, "ISessionResource OnSessionFreed 异常", new { resource = r.GetType().Name, error = ex.Message }); }
        }
    }

    public bool AnyHasActiveResources(string sessionId)
    {
        foreach (var r in _resources.Keys)
        {
            try { if (r.HasActiveResources(sessionId)) return true; }
            catch { /* best effort */ }
        }
        return false;
    }
}
```

- [ ] **Step 4: 改造 `SessionEngine.cs`**

在字段区（约 line 38-42）添加：

```csharp
// session-scoped 子资源注册表（子 agent / 下载 / SSH 共用）
private readonly SessionResourceRegistry _sessionResources = new();
```

替换 `ResolveEffectiveParent`（line 317-330）—— 保留同名方法避免 SubAgentAccessAdapter 调用方破坏，内部委托新方法：

```csharp
/// <summary>
/// 跨 0 或多次 compact 转发解析 loopId 当前的活跃 session ID。
/// loopId 非 session: 返回原值。session 已死且无 forward: 返回 null。
/// </summary>
public string? ResolveActiveSession(string loopId)
{
    if (loopId == null) return null;
    if (!loopId.StartsWith("session:", System.StringComparison.OrdinalIgnoreCase))
        return loopId; // channel: / task: / review: 原样返回

    var current = loopId.Substring("session:".Length);
    while (_forwards.TryGetValue(current, out var forwarded))
        current = forwarded;
    if (_sessions.ContainsKey(current)) return $"session:{current}";
    return null;
}

/// <summary>子 agent 兼容入口，返回 effective parent session ID（无前缀）。内部委托 ResolveActiveSession。</summary>
public string? ResolveEffectiveParent(string childId)
{
    var resolved = ResolveActiveSession($"session:{childId}");
    return resolved?.Substring("session:".Length);
}
```

改造 `WakeUpSession`（line 420-425）走 forward：

```csharp
/// <summary>通过外部信号唤醒指定 session（异步任务完成等）。自动跨 compact forward。</summary>
public void WakeUpSession(string sessionId)
{
    var active = ResolveActiveSession($"session:{sessionId}");
    if (active == null) return;
    var bareId = active.Substring("session:".Length);
    if (_sessions.TryGetValue(bareId, out var session))
        session.WakeUp();
}

// 注册表对外入口
public void RegisterSessionResource(AgentLilara.PluginSDK.Services.ISessionResource r) => _sessionResources.Register(r);
public void UnregisterSessionResource(AgentLilara.PluginSDK.Services.ISessionResource r) => _sessionResources.Unregister(r);
internal System.Threading.Tasks.Task NotifyResourcesCompactingAsync(string oldId, string newId) => _sessionResources.NotifyCompactingAsync(oldId, newId);
internal System.Threading.Tasks.Task NotifyResourcesFreedAsync(string id) => _sessionResources.NotifyFreedAsync(id);
internal bool AnyResourceBlocksExit(string sessionId) => _sessionResources.AnyHasActiveResources(sessionId);
```

- [ ] **Step 5: 创建测试辅助 `TestHost.cs`**

```csharp
// AgentCoreProcessor.Tests/TestHost.cs
using System;
using System.Collections.Concurrent;
using AgentCoreProcessor.Engine.Session;
using Moq;

namespace AgentCoreProcessor.Tests;

internal static class TestHost
{
    /// <summary>构造一个 SessionEngine 实例，预置一个 fake session，用于 ResolveActiveSession 等纯状态测试。</summary>
    public static SessionEngine BuildSessionEngineWithFakeSession(string sessionId, bool alive)
    {
        var engine = new SessionEngine(/* 必要最小依赖 */);
        engine.SeedFakeSession(sessionId, alive);
        return engine;
    }
}

// 用 partial/mock 方式注入内部状态——具体依赖 SessionEngine 的构造签名，T2 Step 4 实装时调整。
```

注意：`TestHost` 和 `SeedForward` / `WasWoken` / `SeedFakeSession` 这些测试钩子要么 internal 暴露给测试项目（`InternalsVisibleTo`），要么用 reflection。具体方法在 T2 Step 4 实装时根据 SessionEngine 的真实构造签名决定，目标：不污染生产代码。

- [ ] **Step 6: 编译 + 运行测试**

Run: `dotnet test AgentLilaraProjectSolution/AgentCoreProcessor.Tests --filter SessionResourceRegistry`
Expected: PASS（5 个测试全过）

- [ ] **Step 7: Commit**

```bash
cd AgentLilaraProjectSolution
git add AgentCoreProcessor/Engine/Session/SessionResourceRegistry.cs \
        AgentCoreProcessor/Engine/Session/SessionEngine.cs \
        AgentCoreProcessor.Tests/SessionResourceRegistryTests.cs \
        AgentCoreProcessor.Tests/TestHost.cs
git commit -m "feat: SessionEngine resource registry + ResolveActiveSession forward chain"
```

---

### Task 3: MasterEngine.WakeLoop channel 路径加 Session 占用优先

**Files:**
- Modify: `AgentLilaraProjectSolution/AgentCoreProcessor/Engine/Core/MasterEngine.cs:866-901`
- Test: `AgentLilaraProjectSolution/AgentCoreProcessor.Tests/WakeLoopRoutingTests.cs`

**Interfaces:**
- Consumes: `SessionEngine.GetFullClaimer(channelId)`（已存在）、`SessionEngine.WakeUpSession`（T2 改造）
- Produces: 改造后的 `MasterEngine.WakeLoop("channel:N")` —— T9 定时器路由依赖此行为

- [ ] **Step 1: 写失败测试**

```csharp
// AgentCoreProcessor.Tests/WakeLoopRoutingTests.cs
using AgentCoreProcessor.Engine;
using Xunit;

namespace AgentCoreProcessor.Tests;

public class WakeLoopRoutingTests
{
    [Fact]
    public async Task WakeLoop_channel_wakes_session_when_channel_claimed()
    {
        var host = TestHost.BuildMasterEngineWithChannelClaim(channelId: 5, claimerSession: "S1");
        host.WakeLoop("channel:5");
        Assert.True(host.SessionWasWoken("S1"));
    }

    [Fact]
    public async Task WakeLoop_channel_falls_through_to_express_when_unclaimed()
    {
        var host = TestHost.BuildMasterEngineWithChannelClaim(channelId: 5, claimerSession: null);
        host.WakeLoop("channel:5");
        Assert.True(host.ExpressWasSignaled(channelId: 5));
        Assert.False(host.SessionWasWoken(anyId: true));
    }

    [Fact]
    public async Task WakeLoop_channel_with_dead_claim_falls_through_to_express()
    {
        var host = TestHost.BuildMasterEngineWithChannelClaim(channelId: 5, claimerSession: "S1", sessionAlive: false);
        host.WakeLoop("channel:5");
        Assert.True(host.ExpressWasSignaled(channelId: 5));
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AgentLilaraProjectSolution/AgentCoreProcessor.Tests --filter WakeLoopRouting`
Expected: FAIL（当前实现不查 claim，Express 永远被信号，session 不被 wake）

- [ ] **Step 3: 改造 `MasterEngine.WakeLoop` channel 分支**

在 `MasterEngine.cs:873-889` 把 `IsChannel` 分支改成先查 SessionEngine 是否 claim：

```csharp
if (LoopId.IsChannel(loopId, out var channelId))
{
    // 优先查 SessionEngine 是否 Full-claim 了这个频道
    var sessionEngine = GetActiveEnginesSnapshot()
        .OfType<Session.SessionEngine>().FirstOrDefault();
    if (sessionEngine != null)
    {
        var claimer = sessionEngine.GetFullClaimer(channelId);
        if (!string.IsNullOrEmpty(claimer))
        {
            // claimer 是裸 sessionId
            var active = sessionEngine.ResolveActiveSession($"session:{claimer}");
            if (active != null)
            {
                sessionEngine.WakeUpSession(claimer);
                return;
            }
            // claimer 已死 → 落回 Express（claim 在 ReleaseSessionClaims 中会被清，这里 race 兜底）
        }
    }

    // 没 claim → 走 Express
    var check = GetSpawnCheck<ExpressEngineSpawnCheck>();
    if (check != null)
    {
        var channels = check.GetActiveChannels();
        if (channels.TryGetValue(channelId, out var engine) && engine.IsAlive)
        {
            engine.SignalGate();
        }
        else
        {
            _ = ColdStartChannelAsync(channelId, check);
        }
    }
    return;
}
```

- [ ] **Step 4: 运行测试**

Run: `dotnet test AgentLilaraProjectSolution/AgentCoreProcessor.Tests --filter WakeLoopRouting`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
cd AgentLilaraProjectSolution
git add AgentCoreProcessor/Engine/Core/MasterEngine.cs \
        AgentCoreProcessor.Tests/WakeLoopRoutingTests.cs \
        AgentCoreProcessor.Tests/TestHost.cs
git commit -m "feat: WakeLoop channel routes to Session when claimed"
```

---

### Task 4: RuntimeSession 唤醒后先排空组件通知

**Files:**
- Modify: `AgentLilaraProjectSolution/AgentCoreProcessor/Engine/Session/RuntimeSession.cs:624-694`
- Test: `AgentLilaraProjectSolution/AgentCoreProcessor.Tests/RuntimeSessionNotificationDrainTests.cs`

**Interfaces:**
- Consumes: `ComponentHost.OnBeforeInvokeAsync` / `BuildPromptSections`（已存在）
- Produces: 改造后的 RuntimeSession 主循环——T7/T8/T9 集成测试依赖

- [ ] **Step 1: 写失败测试**

```csharp
// AgentCoreProcessor.Tests/RuntimeSessionNotificationDrainTests.cs
using AgentCoreProcessor.Engine.Session;
using Xunit;

namespace AgentCoreProcessor.Tests;

public class RuntimeSessionNotificationDrainTests
{
    [Fact]
    public async Task WakeUp_with_pending_download_notification_proceeds_to_agent_invoke()
    {
        var host = TestHost.BuildRuntimeSessionWithFakeComponent(out var fakeComp);
        fakeComp.EnqueueNotification("[下载通知] x.zip 完成");
        host.SignalWakeUp();
        await host.RunOneBurstAsync();
        Assert.True(fakeComp.OnBeforeInvokeCalled);
        Assert.Contains("[下载通知]", host.LastInjectedSystemMessage);
    }

    [Fact]
    public async Task WakeUp_with_no_notification_no_no_input_returns_to_wait()
    {
        var host = TestHost.BuildRuntimeSessionWithFakeComponent(out var fakeComp);
        host.SignalWakeUp();
        await host.RunOneBurstAsync();
        Assert.False(host.AgentInvoked);
        Assert.True(host.IsAwaitingInput);
    }
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AgentLilaraProjectSolution/AgentCoreProcessor.Tests --filter RuntimeSessionNotificationDrain`
Expected: FAIL（当前 line 642-646：drained.Count==0 && _pendingInjections.Count==0 → continue → 永远到不了 702 的 OnBeforeInvoke）

- [ ] **Step 3: 改造 `RuntimeSession.cs:624-694` 主循环**

把 `WaitForActivationAsync` 返回后的处理改成「先排空组件通知」：

```csharp
if (awaitingInput)
{
    if (!await WaitForActivationAsync(_cts.Token))
        break;

    TouchActivity();
    SetWaitUntil(null);
    awaitingInput = false;

    agentRound = 0;
    _roundCount++;
    ApplyRoundPricing();

    // ── 先排空组件通知（异步任务完成：下载/SSH/日程等）──
    bool hasComponentNotification = false;
    if (_componentHost != null)
    {
        await _componentHost.OnBeforeInvokeAsync();
        var sections = _componentHost.BuildPromptSections();
        if (sections.Count > 0)
        {
            var notifyMsg = new Message { Role = "user", Content = "[组件通知]\n" + string.Join("\n\n", sections) };
            _history.Add(notifyMsg);
            await PersistRoundAsync(new List<Message> { notifyMsg });
            Signal.Event(LogGroup.Engine, "组件通知注入(突发入口)",
                new { sessionId = SessionId, sectionCount = sections.Count });
            hasComponentNotification = true;
        }
    }

    // 收集输入
    var drained = DrainInputPortsRaw(ct);
    if (drained.Count == 0 && _pendingInjections.Count == 0 && !hasComponentNotification)
    {
        awaitingInput = true;
        continue;
    }

    if (_host != null)
    {
        // ... 原有 BuildRoundAsync 逻辑不变 ...
    }
    else
    {
        // ... fallback 不变 ...
    }
}
```

注意：原 line 700-711「Agent 推理前」的 `OnBeforeInvokeAsync` + `BuildPromptSections` 逻辑保留——它仍处理本轮中途到达的通知，且 idempotent（第二次 OnBeforeInvoke 返回空队列）。但为了避免重复注入：在 Agent 推理前的代码块加一个判断 `if (!hasComponentNotification) { ... 原逻辑 ... }`，或更稳妥的做法是把突发入口处的排空移到这里之前、Agent 推理前只 drain 新到的。

实装时具体如何去重由实施者按 idempotency 决定，但测试 `RuntimeSessionNotificationDrainTests` 必须通过。

- [ ] **Step 4: 运行测试**

Run: `dotnet test AgentLilaraProjectSolution/AgentCoreProcessor.Tests --filter RuntimeSessionNotificationDrain`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
cd AgentLilaraProjectSolution
git add AgentCoreProcessor/Engine/Session/RuntimeSession.cs \
        AgentCoreProcessor.Tests/RuntimeSessionNotificationDrainTests.cs \
        AgentCoreProcessor.Tests/TestHost.cs
git commit -m "fix: drain component notifications before returning to wait"
```

---

### Task 5: CreateFromCompact 触发 rebind + StopSession/ForceKill 通知 detach

**Files:**
- Modify: `AgentLilaraProjectSolution/AgentCoreProcessor/Engine/Session/SessionEngine.cs:241-279`（CreateFromCompact）、`:281-305`（StopSession/ForceKill）

**Interfaces:**
- Consumes: `SessionResourceRegistry.NotifyCompactingAsync` / `NotifyFreedAsync`（T2 产出）
- Produces: compact 时通知 ISessionResource rebind —— T6/T7/T8 的资源实装依赖此行为

- [ ] **Step 1: 写失败测试**

```csharp
// 在 SessionResourceRegistryTests.cs 追加
[Fact]
public async Task CreateFromCompact_notifies_resources_to_rebind()
{
    var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);
    var resource = new FakeResource();
    engine.RegisterSessionResource(resource);

    engine.SimulateCompact("S1", "S2"); // 测试包装 CreateFromCompact 的最小路径

    Assert.Equal("S1", resource.LastRebindOld);
    Assert.Equal("S2", resource.LastRebindNew);
}

[Fact]
public async Task StopSession_notifies_resources_freed()
{
    var engine = TestHost.BuildSessionEngineWithFakeSession("S1", alive: true);
    var resource = new FakeResource();
    engine.RegisterSessionResource(resource);

    engine.StopSession("S1", "test");

    Assert.Equal("S1", resource.LastFreed);
}
```

- [ ] **Step 2: 运行测试确认失败**

Run: `dotnet test AgentLilaraProjectSolution/AgentCoreProcessor.Tests --filter SessionResourceRegistry`
Expected: FAIL（新增两个测试，rebind/freed 回调未触发）

- [ ] **Step 3: 改造 `CreateFromCompact`**

在 `SessionEngine.cs:274`（现有"子链迁移"那段）后追加：

```csharp
// 通知所有 ISessionResource：loopId 从旧 session rebind 到新 session
await _sessionResources.NotifyCompactingAsync(oldSession.SessionId, newSession.SessionId);
```

注意：`CreateFromCompact` 当前不是 async —— 改签名 `public async Task<IRuntimeSession> CreateFromCompact(...)`，调用方 `CompactTool.ExecuteAsync` line 63 已经在 async 上下文里，改 `_sessionEngine.CreateFromCompact(_session, summary)` 加 await 即可。

- [ ] **Step 4: 改造 `StopSession` / `ForceKill`**

`StopSession`（line 281-293）末尾、`ReleaseSessionClaims` 之后：

```csharp
public async Task<StopResult> StopSession(string id, string reason)
{
    if (!_sessions.TryGetValue(id, out var session))
        return new StopResult { Success = false, BlockReason = "Not found" };

    var children = GetActiveChildren(id);
    if (children != null && children.Count > 0)
        return new StopResult { Success = false, BlockReason = "Has running children", BlockingChildren = children.ToList() };

    // 资源阻塞检查（下载/SSH in-flight）
    if (_sessionResources.AnyHasActiveResources(id))
        return new StopResult { Success = false, BlockReason = "Has active resources" };

    session.RequestStop(reason);
    ReleaseSessionClaims(id);
    await _sessionResources.NotifyFreedAsync(id);
    return new StopResult { Success = true };
}
```

`ForceKill`（line 295-305）类似追加 NotifyFreedAsync。

注意：这俩方法签名改 async 后，调用方 `SubAgentAccessAdapter.RequestStop` / `SystemOpsComponent.StopSubAgentTool` 等可能需要加 await。**寻找所有调用方并同步修改。**

- [ ] **Step 5: 运行测试**

Run: `dotnet test AgentLilaraProjectSolution/AgentCoreProcessor.Tests --filter SessionResourceRegistry`
Expected: PASS（7 个测试）

- [ ] **Step 6: 全量编译确认调用方都改好**

Run: `cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null; dotnet build AgentLilaraProjectSolution/AgentLilaraProject.sln`
Expected: PASS（编译无错；若有调用方未 await，编译会报 CS0121 警告或错误）

- [ ] **Step 7: Commit**

```bash
cd AgentLilaraProjectSolution
git add AgentCoreProcessor/Engine/Session/SessionEngine.cs \
        AgentCoreProcessor/Tool/Core/CompactTool.cs \
        AgentCoreProcessor/Component/SubAgentAccessAdapter.cs \
        Plugins/Plugin.SystemTools/Tools/StopSubAgentTool.cs \
        AgentCoreProcessor.Tests/SessionResourceRegistryTests.cs
git commit -m "feat: compact/stop notify ISessionResource rebind/freed"
```

---

### Task 6: SubAgentAccessAdapter 实装 ISessionResource

**Files:**
- Modify: `AgentLilaraProjectSolution/AgentCoreProcessor/Component/SubAgentAccessAdapter.cs`

**Interfaces:**
- Consumes: `ISessionResource` from Task 1、`SessionEngine.RegisterSessionResource` from T2
- Produces: SubAgentAccessAdapter 作为 ISessionResource 注册到 SessionEngine —— 现有 `_childToParent`/`_forwards` 由 SessionEngine 统一持有，Adapter 只持有 mailbox/waitTcs

- [ ] **Step 1: 改造 SubAgentAccessAdapter 实现 ISessionResource**

```csharp
internal class SubAgentAccessAdapter : ISubAgentAccess, AgentLilara.PluginSDK.Services.ISessionResource
{
    // 构造时把自己注册到 SessionEngine
    public SubAgentAccessAdapter(Engine.MasterEngine master)
    {
        _master = master;
        var se = master.GetActiveEnginesSnapshot().OfType<SessionEngine>().FirstOrDefault();
        se?.RegisterSessionResource(this);
    }

    // ISessionResource 实装
    public Task OnSessionCompactingAsync(string oldSessionId, string newSessionId)
    {
        // _childMailboxes / _waitTcs 以 childId 为 key，不直接持有 parent id
        // 子 agent 的父链已经由 SessionEngine._forwards 维护，无需在此处理
        // 这里只是确保接口实现存在
        return Task.CompletedTask;
    }

    public Task OnSessionFreedAsync(string sessionId)
    {
        // 父 session 被释放：找出所有以该 session 为父的子 agent，让它们也终止
        // 现有 SubAgent 链如果没显式 stop_sub_agent，这里兜底
        var childrenToClean = _childMailboxes.Keys
            .Where(cid => GetSessionEngine()?.ResolveEffectiveParent(cid) == sessionId)
            .ToList();
        foreach (var cid in childrenToClean)
        {
            _childMailboxes.TryRemove(cid, out _);
            _waitTcs.TryRemove(cid, out _);
        }
        return Task.CompletedTask;
    }

    public bool HasActiveResources(string sessionId)
    {
        // 父 session 要退出时，看是否有以它为父的活跃子 agent
        var se = GetSessionEngine();
        if (se == null) return false;
        var children = se.GetActiveChildren(sessionId);
        return children != null && children.Count > 0;
    }

    // ... 现有方法不变 ...
}
```

- [ ] **Step 2: 编译**

Run: `dotnet build AgentLilaraProjectSolution/AgentCoreProcessor/AgentCoreProcessor.csproj`
Expected: PASS

- [ ] **Step 3: 集成测试——现有 SubAgentAccessAdapter 测试（如有）必须仍通过**

Run: `dotnet test AgentLilaraProjectSolution/AgentCoreProcessor.Tests --filter SubAgent`
Expected: PASS（若现无测试，本步骤 skip，但需手工 --test 验证 sub-agent 功能仍工作）

- [ ] **Step 4: Commit**

```bash
cd AgentLilaraProjectSolution
git add AgentCoreProcessor/Component/SubAgentAccessAdapter.cs
git commit -m "refactor: SubAgentAccessAdapter implements ISessionResource"
```

---

### Task 7: NetworkTools 改 Disabled + 实装 ISessionResource + sticky

**Files:**
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools/NetworkToolsLoopComponent.cs:7-9`（LoopApplicability）
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools/DownloadStore.cs`（实装 ISessionResource）
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools/NetworkToolsGlobalComponent.cs`（注册资源）

**Interfaces:**
- Consumes: `ISessionResource` from T1、`ISessionResourceAccess` from T1（通过 GlobalComponentContext.GetService）
- Produces: 下载循环链路全通 —— T11 端到端验证

- [ ] **Step 1: 改 LoopApplicability**

`NetworkToolsLoopComponent.cs:7-9`：

```csharp
[Component(Name = "network-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Disabled,
    Review = Applicability.Disabled, Task = Applicability.Enabled)]
public class NetworkToolsLoopComponent : LoopComponentBase
```

这意味着 ExpressEngine 的 ComponentHost 不会加载此组件（ComponentConfig.IsEnabled 会返回 false），下载/HTTP 工具在 Express 模式下不可见。HTTP 工具若仍想保留在 Express，可拆出独立组件——本计划不做拆分，HTTP 也一并只走 Session。

- [ ] **Step 2: 改造 DownloadStore 实装 ISessionResource**

```csharp
// DownloadStore.cs
using AgentLilara.PluginSDK.Services;

public class DownloadStore : ISessionResource
{
    // ... 现有字段不变 ...

    // ISessionResource 实装
    public Task OnSessionCompactingAsync(string oldSessionId, string newSessionId)
    {
        var oldLoopId = $"session:{oldSessionId}";
        var newLoopId = $"session:{newSessionId}";
        foreach (var task in _tasks.Values)
        {
            if (task.LoopId == oldLoopId)
            {
                // 注意：DownloadTask.LoopId 是 init-only，需改成 set
                task.RebindLoopId(newLoopId);
            }
        }
        // 通知队列也要改 key
        if (_notifications.TryGetValue(oldLoopId, out var queue))
        {
            _notifications.TryRemove(oldLoopId, out _);
            _notifications[newLoopId] = queue;
        }
        return Task.CompletedTask;
    }

    public Task OnSessionFreedAsync(string sessionId)
    {
        var loopId = $"session:{sessionId}";
        // 通知丢就丢，与 sub-agent 一致
        _notifications.TryRemove(loopId, out _);
        // 不取消正在下载的任务——下载是外部副作用，让它跑完，但通知发出去没人收
        // 与 sub-agent 一致：父死子也死只在 sub-agent 场景；下载让它自然完成
        return Task.CompletedTask;
    }

    public bool HasActiveResources(string sessionId)
    {
        var loopId = $"session:{sessionId}";
        return _tasks.Values.Any(t => t.LoopId == loopId
            && (t.Status == DownloadStatus.Downloading || t.Status == DownloadStatus.Pending));
    }
}
```

- [ ] **Step 3: DownloadTask.LoopId 改可 set**

`DownloadTask.cs:12`：

```csharp
public string LoopId { get; set; } = "";  // init 改成 set，供 rebind
public void RebindLoopId(string newLoopId) => LoopId = newLoopId;
```

`RebindLoopId` 留作显式入口，便于 grep。

- [ ] **Step 4: NetworkToolsGlobalComponent 注册 DownloadStore**

```csharp
// NetworkToolsGlobalComponent.cs:OnInitAsync
public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
{
    // ... 现有逻辑 ...
    _store = new DownloadStore { MaxConcurrent = _security.MaxConcurrentDownloads };

    // 注册到 SessionEngine 的子资源表
    var resourceAccess = context.GetService<ISessionResourceAccess>();
    resourceAccess?.Register(_store);

    // ... 现有 HttpClient/Notifier 逻辑 ...
}
```

`OnShutdownAsync` 反注册：

```csharp
var resourceAccess = _ctx.GetService<ISessionResourceAccess>();
resourceAccess?.Unregister(_store);
```

- [ ] **Step 5: 编译 NetworkTools 插件**

Run: `dotnet build AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools/Plugin.NetworkTools.csproj`
Expected: PASS（编译触发 CopyToHostPlugins）

- [ ] **Step 6: Commit**

```bash
cd AgentLilaraProjectSolution
git add Plugins/Plugin.NetworkTools/NetworkToolsLoopComponent.cs \
        Plugins/Plugin.NetworkTools/DownloadStore.cs \
        Plugins/Plugin.NetworkTools/DownloadTask.cs \
        Plugins/Plugin.NetworkTools/NetworkToolsGlobalComponent.cs
git commit -m "feat: NetworkTools channel disabled + DownloadStore as ISessionResource"
```

---

### Task 8: SshTools 改 Disabled + 实装 ISessionResource + sticky

**Files:**
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.SshTools/SshLoopComponent.cs:7-9`
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.SshTools/SshGlobalComponent.cs`（实装 ISessionResource、HasActiveTasks）
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.SshTools/SshTask.cs:9`（LoopId 改 set）

**Interfaces:**
- Consumes: `ISessionResource` from T1、`ISessionResourceAccess` from T1
- Produces: SSH 循环链路全通

- [ ] **Step 1: 改 LoopApplicability**

`SshLoopComponent.cs:7-9`：

```csharp
[Component(Name = "ssh-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Disabled,
    Review = Applicability.Disabled, Task = Applicability.Enabled)]
public class SshLoopComponent : LoopComponentBase
```

- [ ] **Step 2: SshTask.LoopId 改 set**

`SshTask.cs:9`：

```csharp
public string LoopId { get; set; } = "";
public void RebindLoopId(string newLoopId) => LoopId = newLoopId;
```

- [ ] **Step 3: SshGlobalComponent 实装 ISessionResource**

```csharp
// SshGlobalComponent.cs
public class SshGlobalComponent : GlobalComponentBase, AgentLilara.PluginSDK.Services.ISessionResource
{
    // ... 现有字段 ...

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        // ... 现有逻辑 ...
        var resourceAccess = context.GetService<AgentLilara.PluginSDK.Services.ISessionResourceAccess>();
        resourceAccess?.Register(this);
        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        var resourceAccess = _ctx.GetService<AgentLilara.PluginSDK.Services.ISessionResourceAccess>();
        resourceAccess?.Unregister(this);
        // ... 现有清理逻辑 ...
    }

    // ISessionResource 实装
    public Task OnSessionCompactingAsync(string oldSessionId, string newSessionId)
    {
        var oldLoopId = $"session:{oldSessionId}";
        var newLoopId = $"session:{newSessionId}";
        foreach (var kvp in _runningTasks)
            if (kvp.Value.LoopId == oldLoopId) kvp.Value.RebindLoopId(newLoopId);
        foreach (var kvp in _completedTasks)
            if (kvp.Value.LoopId == oldLoopId) kvp.Value.RebindLoopId(newLoopId);
        return Task.CompletedTask;
    }

    public Task OnSessionFreedAsync(string sessionId)
    {
        var loopId = $"session:{sessionId}";
        // 不杀 running 任务，与下载一致；通知丢就丢
        return Task.CompletedTask;
    }

    public bool HasActiveResources(string sessionId)
    {
        var loopId = $"session:{sessionId}";
        return _runningTasks.Values.Any(t => t.LoopId == loopId);
    }
}
```

- [ ] **Step 4: 编译 SSH 插件**

Run: `dotnet build AgentLilaraProjectSolution/Plugins/Plugin.SshTools/Plugin.SshTools.csproj`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
cd AgentLilaraProjectSolution
git add Plugins/Plugin.SshTools/SshLoopComponent.cs \
        Plugins/Plugin.SshTools/SshGlobalComponent.cs \
        Plugins/Plugin.SshTools/SshTask.cs
git commit -m "feat: SshTools channel disabled + SshGlobalComponent as ISessionResource"
```

---

### Task 9: ScheduleTaskTool 加 requires_work + 定时器按类型路由

**Files:**
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.ScheduledTasks/ScheduleTaskTool.cs`
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.ScheduledTasks/ScheduledTaskStore.cs`（ScheduledTaskEntry 加 RequiresWork 字段）
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.ScheduledTasks/ScheduledTasksTimerComponent.cs:88-178`（TimerLoop + FindEarliestTask）
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.ScheduledTasks/ScheduledTasksComponent.cs:97-135`（提醒文本体现 requires_work）

**Interfaces:**
- Consumes: `MasterEngine.WakeLoop` 改造后的 channel 路径（T3）、`SessionEngine.GetFullClaimer` / `IsChannelClaimed`（已存在）
- Produces: 日程提醒在 Session 活跃时也能送达 + 区分提一嘴/需工作

- [ ] **Step 1: ScheduledTaskEntry 加 RequiresWork**

`ScheduledTaskStore.cs:9-19`：

```csharp
public class ScheduledTaskEntry
{
    public string Id { get; set; } = "";
    public string Description { get; set; } = "";
    public string Expression { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? NextFireTime { get; set; }
    public DateTime? LastFiredAt { get; set; }
    public bool IsRecurring { get; set; }
    public bool Enabled { get; set; } = true;
    public bool RequiresWork { get; set; } = false;  // 新增
}
```

`AddTask` 改签名：

```csharp
public ScheduledTaskEntry AddTask(string description, string expression, DateTime? nextFire, bool isRecurring, bool requiresWork)
{
    // ... 内部构造 entry 时 RequiresWork = requiresWork ...
}
```

- [ ] **Step 2: ScheduleTaskTool 加参数**

`ScheduleTaskTool.cs:20-25`：

```csharp
public IReadOnlyList<ToolParameter> Parameters =>
[
    new("expression", "时间表达式", 0),
    new("description", "任务描述（触发时AI会看到）", 1),
    new("requires_work", "是否需要工作模式处理（false=只提一嘴即可，true=需调用工具干活）。默认 false", 2, isRequired: false)
];
```

`ExecuteAsync` 解析：

```csharp
var requiresWorkStr = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "false";
bool requiresWork = requiresWorkStr.Equals("true", StringComparison.OrdinalIgnoreCase)
    || requiresWorkStr == "1" || requiresWorkStr == "yes";
// ... 解析成功后 ...
var entry = _store.AddTask(description, expression, result.NextFireTime, result.IsRecurring, requiresWork);
```

- [ ] **Step 3: ScheduledTasksTimerComponent 改造**

`FindEarliestTask` 返回 `(DateTime?, loopId, requiresWork)`：

```csharp
private (DateTime? nextFire, string? loopId, bool requiresWork) FindEarliestTask()
{
    if (!Directory.Exists(_pluginDataBase))
        return (null, null, false);

    DateTime? earliest = null;
    string? earliestLoopId = null;
    bool earliestRequiresWork = false;

    foreach (var dir in Directory.GetDirectories(_pluginDataBase))
    {
        var taskFile = Path.Combine(dir, "scheduled_tasks.json");
        if (!File.Exists(taskFile)) continue;
        var store = new ScheduledTaskStore(dir);
        var (nextFire, requiresWork) = store.GetNextFireTimeWithFlag();
        if (nextFire != null && (earliest == null || nextFire < earliest))
        {
            earliest = nextFire;
            var dirName = Path.GetFileName(dir);
            earliestLoopId = dirName.Replace('_', ':');
            earliestRequiresWork = requiresWork;
        }
    }
    return (earliest, earliestLoopId, earliestRequiresWork);
}
```

`ScheduledTaskStore` 加 `GetNextFireTimeWithFlag`：

```csharp
public (DateTime?, bool) GetNextFireTimeWithFlag()
{
    lock (_lock)
    {
        var candidate = LoadTasks()
            .Where(t => t.Enabled && t.NextFireTime.HasValue)
            .OrderBy(t => t.NextFireTime!.Value)
            .FirstOrDefault();
        return (candidate?.NextFireTime, candidate?.RequiresWork ?? false);
    }
}
```

`TimerLoop` 按类型路由（替换原 line 109-136）：

```csharp
var delay = nextFire.Value - DateTime.Now;
if (delay <= TimeSpan.Zero)
{
    _log?.Event(LogGroup, "global-timer-fire", new { loopId, overdue = -(int)delay.TotalSeconds, requiresWork });
    FireScheduledTask(loopId!, requiresWork);
    try { await Task.Delay(2000, shutdownCt); } catch { break; }
    continue;
}

// ... delay 正常路径 ...
try
{
    await Task.Delay(delay, delayCts.Token);
    _log?.Event(LogGroup, "global-timer-fire", new { loopId, requiresWork });
    FireScheduledTask(loopId!, requiresWork);
}
// ...

private void FireScheduledTask(string loopId, bool requiresWork)
{
    // 检查 channel 是否被 Session claim
    if (loopId.StartsWith("channel:") && int.TryParse(loopId[8..], out var channelId))
    {
        var sessionEngine = _ctx.GetService<AgentLilara.PluginSDK.Services.ISessionResourceAccess>();
        // 先尝试 wake 已 claim 的 session（MasterEngine.WakeLoop channel 分支已处理）
    }

    // 不论是否 claim，都通过 WakeLoop 路由——
    // MasterEngine.WakeLoop 已会判断 channel 被 claim 时优先 wake session（T3 改造）。
    // 若无 session 且 requires_work=true：冷启动 channel session
    if (requiresWork && loopId.StartsWith("channel:"))
    {
        if (!TryEnsureSessionForChannel(loopId))
        {
            // session 创建失败 → 退化为 wake Express
            _ctx.WakeLoop(loopId);
            return;
        }
        // 新 session 已创建，定时任务通知在 ScheduledTasksComponent.OnBeforeInvokeAsync 中会被发现
        return;
    }

    _ctx.WakeLoop(loopId);
}

private bool TryEnsureSessionForChannel(string channelLoopId)
{
    // 调用 SessionEngine.CreateSession 创建 Channel-type session
    // 类似 ExpressCycle.cs:174 的 escalate 路径，但由定时器触发
    // 此处需要 SessionEngine 暴露的 CreateFromExternalTrigger 或直接拿 ISessionResourceAccess 的扩展接口
    // 简化版：先调用 _ctx.WakeLoop(loopId) 让 ExpressEngine 拿到通知，由 Express 模型自己 escalate
    // 完整版：T9 实装时决定要不要新增 SDK 接口
    return false;
}
```

**实装决策点**：`TryEnsureSessionForChannel` 的简化版与完整版之间选择——简化版先靠 Express + 模型 escalate 实现"需工作"路径，完整版则由定时器直接冷启动 channel session。

**推荐简化版**作为 T9 的最小可行实现，完整版留待下次迭代，因为：
1. Express 模型看到 `[定时任务通知]` 且描述带"需工作"语义时，会自己决定 escalate；
2. 完整版需要新 SDK 接口（`ISessionCreationAccess.CreateChannelSession`），改动面扩大。

T9 实装时按简化版做，并在 ScheduledTasksComponent 的通知文本里加一句"若当前在 Express 模式，请 escalate 到工作模式处理"。

- [ ] **Step 4: ScheduledTasksComponent 通知文本体现 requires_work**

```csharp
// ScheduledTasksComponent.cs:CheckDueTasksSync
foreach (var task in dueTasks)
{
    // ... 现有逻辑 ...
    notifications.Add(task.RequiresWork
        ? $"{task.Description}（需工作模式处理）"
        : task.Description);
}
// ...
var lines = new List<string> { "[定时任务通知]" };
foreach (var desc in notifications)
    lines.Add($"- {desc}（已触发）");
lines.Add("处理完后应调用 dismiss_notification 清除通知。");
lines.Add("若当前不在工作模式且任务需工作模式处理，请先 escalate。");  // 新增
```

`CheckDueTasksSync` 需要先从 store 读出 `RequiresWork` 字段——`LoadAndFindDue` 已经返回完整 `ScheduledTaskEntry`，直接 `task.RequiresWork`。

- [ ] **Step 5: ScheduledTaskStore 旧 JSON 兼容**

旧的 `scheduled_tasks.json` 文件没有 `RequiresWork` 字段。`LoadTasks` 反序列化时默认 false 即可——`System.Text.Json` 默认行为已处理，无需额外代码。

但旧文件若有 `scheduled_tasks.json.bak` 备份目录，需要确认 `LoadTasks` 在所有可能存在 `scheduled_tasks.json` 的循环目录上都能反序列化。T9 Step 5 需手工跑一次 `--test` 验证旧文件不爆。

- [ ] **Step 6: 编译**

Run: `dotnet build AgentLilaraProjectSolution/Plugins/Plugin.ScheduledTasks/Plugin.ScheduledTasks.csproj`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
cd AgentLilaraProjectSolution
git add Plugins/Plugin.ScheduledTasks/ScheduleTaskTool.cs \
        Plugins/Plugin.ScheduledTasks/ScheduledTaskStore.cs \
        Plugins/Plugin.ScheduledTasks/ScheduledTasksTimerComponent.cs \
        Plugins/Plugin.ScheduledTasks/ScheduledTasksComponent.cs
git commit -m "feat: schedule_task requires_work flag + timer routing"
```

---

### Task 10: ILoopComponent.OnCanExitAsync + ComponentHost.QueryCanExitAsync + end_session 拦截

**Files:**
- Modify: `AgentLilaraProjectSolution/AgentLilara.PluginSDK/ILoopComponent.cs`（加接口方法）
- Modify: `AgentLilaraProjectSolution/AgentLilara.PluginSDK/ComponentBase.cs`（默认实现）
- Modify: `AgentLilaraProjectSolution/AgentCoreProcessor/Component/ComponentHost.cs`（聚合 QueryCanExitAsync）
- Modify: `AgentLilaraProjectSolution/AgentCoreProcessor/Engine/Session/RuntimeSession.cs:434-442`（end_session 分支增查）
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.NetworkTools/NetworkToolsLoopComponent.cs`（实装 OnCanExitAsync）
- Modify: `AgentLilaraProjectSolution/Plugins/Plugin.SshTools/SshLoopComponent.cs`（实装 OnCanExitAsync）

**Interfaces:**
- Consumes: `DownloadStore.HasActiveResources`（T7）、`SshGlobalComponent.HasActiveResources`（T8）
- Produces: end_session 在有 in-flight 下载/SSH 时被拦截 —— T11 端到端验证

- [ ] **Step 1: 加 ILoopComponent.OnCanExitAsync**

`ILoopComponent.cs`：

```csharp
public interface ILoopComponent
{
    // ... 现有成员 ...
    /// <summary>所属 session 准备 end_session 时被询问。返回 false 阻止退出，注入提醒文本由调用方做。</summary>
    Task<bool> OnCanExitAsync();
}
```

`ComponentBase.cs:LoopComponentBase`：

```csharp
public virtual Task<bool> OnCanExitAsync() => Task.FromResult(true);
```

- [ ] **Step 2: ComponentHost 加 QueryCanExitAsync**

```csharp
// ComponentHost.cs
public async Task<bool> QueryCanExitAsync()
{
    foreach (var inst in _loopComponents.Where(i => i.Context.IsEnabled))
    {
        try { if (!await inst.Component.OnCanExitAsync()) return false; }
        catch (Exception ex) { LogError(inst, "OnCanExit", ex); }
    }
    return true;
}
```

- [ ] **Step 3: RuntimeSession.ApplyToolEffectAsync end_session 分支加拦截**

`RuntimeSession.cs:434-442`：

```csharp
case "end_session":
    // 子 agent 检查（现有）
    var activeChildren = _sessionEngine?.GetActiveChildren(SessionId);
    if (activeChildren != null && activeChildren.Count > 0)
    {
        InjectExternal($"[系统警告] 你还有 {activeChildren.Count} 个活跃子 agent，请先 stop_sub_agent 后再 end_session。");
        return new ToolEffect(false, false);
    }

    // session-scoped 子资源检查（下载/SSH in-flight）
    if (_sessionEngine?.AnyResourceBlocksExit(SessionId) == true
        || !await (_componentHost?.QueryCanExitAsync() ?? Task.FromResult(true)))
    {
        // 二次 end_session 强制退出
        if (_forceExitAttempts > 0)
        {
            Signal.Event(LogGroup.Engine, "end_session force override", new { sessionId = SessionId });
            await SendOutput(result.Data ?? "工作完成（强制退出，仍有未完成资源）。");
            return new ToolEffect(false, true);
        }
        _forceExitAttempts++;
        InjectExternal("[系统警告] 仍有未完成的下载或 SSH 任务。请先 cancel_download/cancel ssh 任务后再 end_session；或再次 end_session 强制退出（不推荐）。");
        return new ToolEffect(false, false);
    }

    await SendOutput(result.Data ?? "工作完成。");
    return new ToolEffect(false, true);
```

新增字段：

```csharp
private int _forceExitAttempts = 0;  // 0=未触发，>=1=已拦截过一次
```

每次 `endBurst=true` 进入新突发时重置（`awaitingInput = true; continue;` 之前），让用户下次 `end_session` 是新的拦截周期。具体实装在 line 873 `awaitingInput = true` 之前加 `_forceExitAttempts = 0;`。

- [ ] **Step 4: NetworkToolsLoopComponent 实装 OnCanExitAsync**

```csharp
public override Task<bool> OnCanExitAsync()
{
    var store = NetworkToolsAccessor.Store;
    if (store == null) return Task.FromResult(true);
    var loopId = _loopId;
    // HasActiveResources 接受裸 sessionId，loopId 是 "session:GUID" 或 "channel:N"
    // SessionEngine.AnyResourceBlocksExit 接受裸 sessionId；这里我们直接查 store
    var bareId = loopId.StartsWith("session:") ? loopId[8..] : null;
    if (bareId == null) return Task.FromResult(true); // Channel loop: 不阻塞
    return Task.FromResult(!store.HasActiveResources(bareId));
}
```

- [ ] **Step 5: SshLoopComponent 实装 OnCanExitAsync**

```csharp
public override Task<bool> OnCanExitAsync()
{
    var global = SshToolsAccessor.Global;
    if (global == null) return Task.FromResult(true);
    var bareId = _loopId.StartsWith("session:") ? _loopId[8..] : null;
    if (bareId == null) return Task.FromResult(true);
    return Task.FromResult(!global.HasActiveResources(bareId));
}
```

- [ ] **Step 6: 全量编译**

Run: `cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null; dotnet build AgentLilaraProjectSolution/AgentLilaraProject.sln`
Expected: PASS（SDK + Core + 三个插件都要编译通过）

- [ ] **Step 7: Commit**

```bash
cd AgentLilaraProjectSolution
git add AgentLilara.PluginSDK/ILoopComponent.cs \
        AgentLilara.PluginSDK/ComponentBase.cs \
        AgentCoreProcessor/Component/ComponentHost.cs \
        AgentCoreProcessor/Engine/Session/RuntimeSession.cs \
        Plugins/Plugin.NetworkTools/NetworkToolsLoopComponent.cs \
        Plugins/Plugin.SshTools/SshLoopComponent.cs
git commit -m "feat: end_session sticky guard via OnCanExitAsync"
```

---

### Task 11: 端到端 build + --test 试运行 + 文档

**Files:**
- Modify: `AgentLilaraProjectSolution/CLAUDE.md`（更新"异步唤醒链路"段）
- Modify: `AgentLilaraProjectSolution/docs/architecture-map.md`（加 ISessionResource 抽象）
- Modify: `AgentLilaraProjectSolution/docs/plugins/patterns.md`（加 SessionResource 模式条目）
- Create: `~/.claude/projects/E--Workspace-AgentLilaraProject/memory/project_async_wakeup_refactor.md`

**Interfaces:**
- Consumes: 所有前述任务的产出

- [ ] **Step 1: 全量 build + 启动**

Run: `cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null; cd AgentLilaraProjectSolution && dotnet build AgentLilaraProject.sln && dotnet run --project AgentCoreProcessor -- --test --delay 500`
Expected: 启动成功，日志显示插件加载完成，无 error

- [ ] **Step 2: 端到端验证清单（手工 --test）**

| 场景 | 期望 |
|------|------|
| 1. 在 Session 模式发起下载 → 等 1 分钟 → 完成通知注入 _history | 模型下一轮看到 `[组件通知] [下载通知] x.zip 完成` 并主动处理 |
| 2. 在 Session 模式发起下载 → 立即 end_session | 被拦截，注入 warning，模型需 cancel 或二次 end_session |
| 3. 在 Session 模式发起下载 → compact 后下载完成 | 通知注入到新 session 的 _history |
| 4. 在 Express 模式发起 download_file | 工具不可见（LoopApplicability Channel=Disabled），模型应 escalate |
| 5. 定时任务 requires_work=false 到期、无 Session | Express 被唤醒，speak 提醒 |
| 6. 定时任务 requires_work=true 到期、无 Session | Express 被唤醒，模型看到"需工作"自己 escalate |
| 7. 定时任务到期、Session 活跃 | Session 被唤醒（MasterEngine.WakeLoop channel 路由） |

每条手工验证，记录到 plan 同名日志文件 `docs/superpowers/plans/2026-07-17-async-wakeup-refactor.log`。

- [ ] **Step 3: 更新 CLAUDE.md**

把"异步唤醒链路"段更新为：

```
- 异步唤醒链路（session-scoped）：
  后台任务完成 → GlobalComponent 回调 ISessionResourceAccess.ResolveActiveSession(loopId) 解出活跃 session → SessionEngine.WakeUpSession → RuntimeSession.WakeUp（_wakeSignaled）→ WaitForActivationAsync 检测 → 突发入口先跑 OnBeforeInvokeAsync + BuildPromptSections → 注入 _history → 进入 Agent 推理
  - session-scoped 子资源：子 agent / 下载 / SSH 实装 ISessionResource，注册到 SessionEngine；compact 时 rebind loopId，session 死时通知清理
  - end_session 拦截：子 agent + 任意 ISessionResource HasActiveResources=true 都阻止退出；二次 end_session force override
- 异步唤醒链路（channel-scoped）：
  定时任务到期 → ScheduledTasksTimerComponent 按 requires_work 路由 → MasterEngine.WakeLoop(channel:N)（内置 Session 占用优先检查）→ claim 则 wake session / 否则 wake Express / 冷启动 Express
```

- [ ] **Step 4: 更新 architecture-map.md / patterns.md**

在 `docs/architecture-map.md` 加一节"Session-scoped 子资源"：

```
子 agent / 下载 / SSH 任务都实装 ISessionResource，注册到 SessionEngine。
SessionEngine 持有 _sessionResources 注册表 + _forwards 转发表：
- compact 时通知所有 ISessionResource.OnSessionCompacting(oldId, newId)，资源改自己的 loopId
- session 死时通知 OnSessionFreed，资源清理状态
- 外部唤醒通过 ResolveActiveSession(loopId) 跨 forward 解出活跃 session
```

在 `docs/plugins/patterns.md` 加新条目 "Session-scoped 资源 (ISessionResource)"。

- [ ] **Step 5: 写 memory 文件**

`~/.claude/projects/E--Workspace-AgentLilaraProject/memory/project_async_wakeup_refactor.md`：

```markdown
---
name: async-wakeup-refactor
description: 异步唤醒链路重构（2026-07-17）：ISessionResource 统一抽象 + channel 路由 + requires_work
metadata:
  type: project
---

2026-07-17 完成：把子 agent / 下载 / SSH 三类 session-scoped 子资源统一到 ISessionResource 接口，
SessionEngine 持有注册表 + forward 转发链，compact 时通知 rebind，死亡时通知清理。
channel-scoped 的日程提醒保留独立路径，通过 MasterEngine.WakeLoop channel 分支自动感知 Session 占用。
end_session 通过 ILoopComponent.OnCanExitAsync 阻塞有 in-flight 资源的退出。

**Why:** 之前下载/SSH 完成通知在 Express 模式下完全失效（ExpressEngine 不调 OnBeforeInvokeAsync），
在 Session 模式下 Session 被唤醒后立即睡回（早返回之前不排空通知），导致你说的"下载提醒不工作、SSH 异步失效"。
日程在 Session 活跃期间完全丢失（定时器只 wake channel，Express 被占用跳过）。

**How to apply:** 新增 session-scoped 异步功能时实装 ISessionResource，OnInitAsync 注册到 SessionEngine，
在 OnSessionCompactingAsync 里改自己的 loopId 引用，OnSessionFreedAsync 清理状态。channel-scoped 的功能（如日程）
仍按 channel:N 路由，依赖 MasterEngine.WakeLoop 自动感知占用。
```

在 `MEMORY.md` 索引加一行：

```
- [异步唤醒重构](project_async_wakeup_refactor.md) — ISessionResource 统一子资源抽象 + channel 路由 + requires_work（2026-07-17 完成）
```

- [ ] **Step 6: Commit**

```bash
cd AgentLilaraProjectSolution
git add CLAUDE.md docs/architecture-map.md docs/plugins/patterns.md docs/superpowers/plans/2026-07-17-async-wakeup-refactor.md docs/superpowers/plans/2026-07-17-async-wakeup-refactor.log
git commit -m "docs: async wakeup refactor documentation"
```

---

## Self-Review

### 1. Spec coverage

- ✅ Q1（接口位置） → T1 在 `Services/ISessionResourceAccess.cs`
- ✅ Q2（SubAgent 改造） → T6
- ✅ Q3（ResolveActiveSession 公共方法 + WakeUpSession 内置转发） → T2 Step 4
- ✅ Q4（不搞孤儿队列，与 sub-agent 一致） → T7/T8 OnSessionFreedAsync 注释明确"丢就丢"
- ✅ 断裂点1（Express 不排空通知） → T7/T8 LoopApplicability Channel=Disabled 让 Express 不再加载这些组件；channel-scoped 日程靠 T3 WakeLoop 路由 → T4 Session 排空
- ✅ 断裂点2（Session 早返回） → T4
- ✅ 日程区分提一嘴/需工作 → T9
- ✅ sticky 阻塞 → T10
- ✅ Compact 继承 → T5 + T7/T8 OnSessionCompactingAsync

### 2. Placeholder scan

- T2 Step 5 `TestHost`：明确说"具体实装时根据 SessionEngine 真实构造签名决定"，不是 TBD——有明确方向（InternalsVisibleTo 或 reflection），实施者按实际签名调整。✅
- T9 Step 3 `TryEnsureSessionForChannel`：明确简化版决策，完整版留待下次迭代。✅
- T11 Step 2 端到端验证：7 条具体场景，不是"适当测试"。✅
- 无 "TODO"、"TBD"、"implement later"。

### 3. Type consistency

- `ISessionResource` 方法名 `OnSessionCompactingAsync` / `OnSessionFreedAsync` / `HasActiveResources` 在 T1 定义，T2/T5/T6/T7/T8/T10 全部一致
- `ResolveActiveSession` 在 T2 定义、T3/T9 使用
- `RegisterSessionResource` / `UnregisterSessionResource` 在 T2 定义、T6/T7/T8 使用
- `OnCanExitAsync` 在 T10 定义，ComponentHost.QueryCanExitAsync 一致
- `RequiresWork` 字段名在 ScheduledTaskStore / ScheduleTaskTool / ScheduledTasksTimerComponent 一致

## 执行提示

- 跨 6 个项目改动，每完成一个 Task 必须立即 commit，便于回滚
- T2/T3/T4 是核心，建议串行；T6/T7/T8 可以并行（但都在 T5 之后）
- T11 是验证 + 文档，必须最后做
