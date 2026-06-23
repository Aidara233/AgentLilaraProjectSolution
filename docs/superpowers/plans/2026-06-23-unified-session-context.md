# 统一会话上下文机器 实现计划（第一批）

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:subagent-driven-development（推荐）或 superpowers:executing-plans 逐任务实现。步骤用 `- [ ]` 复选框追踪。

**Goal:** 抽出共享的 `ChannelContextService`，让 ChannelEngine 的 Express 和 RuntimeSession 用同一套机器构建上下文；统一 session 初始化（working/task 共用一条路径，差异全在 spec）；参与者从"累积状态"降级为"DB 派生视图"；`WorkingCore` 配置改名为 `SessionCore`。

**Architecture:** 三层架构（框架层 ChannelEngine + 基础设施引擎 + 会话层 RuntimeSession）。本批解决"session 没有正确上下文/提示词"这一地基问题，**不含**管道接通（入向 AdapterEngine→MessageEngine、出向订阅、tool_use 配对）——那是第二批，依赖本批先立住。核心模型：**session 的认知 = 它挂载的频道端口集合（每根端口贡献 历史+参与者+记忆+频道信息，全部 DB 派生）+ InitialState 当前指令**。

**Tech Stack:** .NET 8, C#, SQLite-net-pcl, Newtonsoft.Json, xUnit, AgentLilara.PluginSDK

## Global Constraints

- 目标框架：net8.0；主项目 Microsoft.NET.Sdk.Web，SelfContained=true / RID win-x64
- 代码注释/讨论：中文；commit message：英文；类名/方法名/变量名：英文
- Storage 目录不在解决方案文件夹下，无 git 仓库；git 仓库在 solution 目录内
- 改引擎后必须**全量** build（先杀进程）：`cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null; dotnet build`
- 验证分两层：纯逻辑用 `dotnet test`（xUnit），集成行为用 `dotnet run -- --test` 观察日志确认 Express 不退化
- forceToolCall=true 是**期望行为**（纯文本输出=啥也没发生），改名后保留
- 复用现有 ILoopComponent 组件体系，不引入新接口
- 每个任务结束即 `git commit`（仓库在 solution 内）；新项目用 `dotnet sln add` 注册，不手编 .sln

## 已知工具异常

`AgentCoreProcessor/Engine/Worker/ChannelEngine.cs`（约 1025 行）规划期间多次 Read/Grep content 返回损坏占位符。涉及该文件的步骤，执行时**先成功 Read 确认实际行号再 Edit**；本计划给目标形态和锚点方法名，不写死行号。`ChannelContextBuilder.cs`（同 partial class，894 行）读取正常。

---

## File Structure

**新建：**
- `AgentCoreProcessor.Tests/AgentCoreProcessor.Tests.csproj` — xUnit 测试项目（只测本批产出的纯逻辑）
- `AgentCoreProcessor/Engine/Context/ChannelContextState.cs` — 可变上下文状态对象（游标/已注入集合/摘要/缓存前缀），各消费者各持一个
- `AgentCoreProcessor/Engine/Context/ChannelContextService.cs` — 格式化服务，操作显式传入的 state + channelId，从 DB 派生一切
- `AgentCoreProcessor/Engine/Session/SessionAgentHost.cs` — RuntimeSession 的 IAgentHost 实现，委托 ChannelContextService
- `Storage/Core/SessionCore.json` — 由 WorkingCore.json 改名（Storage 操作，非 git）

**修改：**
- `AgentCoreProcessor/AgentCoreProcessor.csproj` — 加 `InternalsVisibleTo("AgentCoreProcessor.Tests")`
- `AgentCoreProcessor/Engine/Worker/ChannelContextBuilder.cs` — 可复用格式化 helper 迁入 service，ChannelEngine 侧委托
- `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs` — BuildStartInject/BuildRoundInject 委托 service；删除 recentParticipants 累积
- `AgentCoreProcessor/Core/AgentCore.cs` — `WorkingCore` → `SessionCore`（默认构造 + SwitchMode 映射 + 注释）
- `AgentCoreProcessor/Command/ConfigCommand.cs` — working 键映射改 SessionCore.json
- `AgentCoreProcessor/WebUI/Providers/ConfigProvider.cs` — 配置编辑器映射改名
- `AgentCoreProcessor/WebUI/Providers/TestProvider.cs` — mock 日志文案改名（cosmetic）
- `AgentLilara.PluginSDK/CreateSessionSpec.cs` — 加 `CoreConfigName` 字段
- `AgentLilara.PluginSDK/PortSpec.cs` — 加 `PreloadHistory` 字段
- `AgentCoreProcessor/Engine/Session/RuntimeSession.cs` — 用 SessionAgentHost 构建上下文
- `AgentCoreProcessor/Engine/Session/SessionEngine.cs` — CreateSession 用正确 loopId + 传 CoreConfigName

---

## Phase 0：测试脚手架

### Task 0：xUnit 测试项目 + InternalsVisibleTo

**Files:**
- Create: `AgentCoreProcessor.Tests/AgentCoreProcessor.Tests.csproj`, `AgentCoreProcessor.Tests/SmokeTest.cs`
- Modify: `AgentCoreProcessor/AgentCoreProcessor.csproj`

**Interfaces:**
- Produces: 能 `dotnet test` 跑通、可访问主程序 `internal` 类型的测试项目。后续所有 Task 的测试加到此项目。

- [ ] **Step 1: 测试项目 csproj**

```xml
<!-- AgentCoreProcessor.Tests/AgentCoreProcessor.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <SelfContained>false</SelfContained>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\AgentCoreProcessor\AgentCoreProcessor.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 主项目暴露 internal**

在 `AgentCoreProcessor/AgentCoreProcessor.csproj` 第一个 `</PropertyGroup>` 之后插入：

```xml
  <ItemGroup>
    <InternalsVisibleTo Include="AgentCoreProcessor.Tests" />
  </ItemGroup>
```

- [ ] **Step 3: 冒烟测试**

```csharp
// AgentCoreProcessor.Tests/SmokeTest.cs
namespace AgentCoreProcessor.Tests;

public class SmokeTest
{
    [Fact]
    public void TestProjectCompilesAndRuns() => Assert.True(true);
}
```

- [ ] **Step 4: 注册 solution**

Run: `cd AgentLilaraProjectSolution && dotnet sln add AgentCoreProcessor.Tests/AgentCoreProcessor.Tests.csproj`
Expected: `已将项目"...AgentCoreProcessor.Tests..."添加到解决方案中`

- [ ] **Step 5: 跑测试**

Run: `dotnet test AgentCoreProcessor.Tests`
Expected: Passed!  - Failed: 0, Passed: 1
> 若报 RID/runtime 还原错误，改 `dotnet test AgentCoreProcessor.Tests -r win-x64`。

- [ ] **Step 6: Commit**

```bash
git add AgentCoreProcessor.Tests/ AgentCoreProcessor/AgentCoreProcessor.csproj AgentLilaraProjectSolution.sln
git commit -m "test: scaffold xUnit test project with InternalsVisibleTo"
```

---

## Phase 1：抽取 ChannelContextState + ChannelContextService

### Task 1：ChannelContextState — 可变状态容器

**Files:**
- Create: `AgentCoreProcessor/Engine/Context/ChannelContextState.cs`

**Interfaces:**
- Produces: `class ChannelContextState`，ChannelEngine 与 RuntimeSession 各持一个实例，传给 service 读写。

- [ ] **Step 1: 创建状态类**

```csharp
// AgentCoreProcessor/Engine/Context/ChannelContextState.cs
using System.Collections.Generic;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>
/// 单个消费者（ChannelEngine 或 RuntimeSession）的频道上下文可变状态。
/// ChannelContextService 操作此对象，自身不持状态。
/// </summary>
internal class ChannelContextState
{
    public int ChannelId { get; set; }
    public string? ChannelName { get; set; }
    public string Mode { get; set; } = "express";   // "express" | "session"
    public string? FixedPrefix { get; set; }
    public string? ContextSummary { get; set; }
    public int LastConsumedMessageId { get; set; }
    public int StartInjectMaxId { get; set; }

    public HashSet<string> RoundImageHashes { get; } = new();
    public HashSet<string> InjectedDescriptions { get; } = new();
    public HashSet<string> InjectedOcrTexts { get; } = new();
}
```

- [ ] **Step 2: 编译验证**

Run: `cd AgentLilaraProjectSolution && dotnet build AgentCoreProcessor -clp:ErrorsOnly`
Expected: 0 错误

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Engine/Context/ChannelContextState.cs
git commit -m "feat: add ChannelContextState for shared context machinery"
```

### Task 2：ChannelContextService — 纯格式化方法（频道信息 + 参与者渲染）

先落地**不依赖 DB 的纯格式化**部分，配单测。重历史/引用/图片方法在 Task 3 迁入。

**Files:**
- Create: `AgentCoreProcessor/Engine/Context/ChannelContextService.cs`
- Create: `AgentCoreProcessor.Tests/ChannelContextServiceTests.cs`

**Interfaces:**
- Consumes: `ChannelContextState`（Task 1）
- Produces:
  - `record ParticipantView(string DisplayName, string? Aliases, string PlatformId, int PersonId, int TrustLevel, string? Memo)`
  - `static string ChannelContextService.RenderChannelInfo(string? channelName, int channelId)`
  - `static string ChannelContextService.RenderParticipants(string? channelName, int channelId, IReadOnlyList<ParticipantView> participants)`

- [ ] **Step 1: 写失败测试**

```csharp
// AgentCoreProcessor.Tests/ChannelContextServiceTests.cs
using AgentCoreProcessor.Engine.Context;

namespace AgentCoreProcessor.Tests;

public class ChannelContextServiceTests
{
    [Fact]
    public void RenderChannelInfo_Group_IncludesGroupId()
    {
        var s = ChannelContextService.RenderChannelInfo("group_12345", 7);
        Assert.Contains("群聊", s);
        Assert.Contains("12345", s);
        Assert.Contains("频道ID: 7", s);
    }

    [Fact]
    public void RenderChannelInfo_Private_IncludesPeerId()
    {
        var s = ChannelContextService.RenderChannelInfo("private_999", 3);
        Assert.Contains("私聊", s);
        Assert.Contains("999", s);
    }

    [Fact]
    public void RenderChannelInfo_Malformed_ReturnsEmpty()
    {
        Assert.Equal("", ChannelContextService.RenderChannelInfo("garbage", 1));
        Assert.Equal("", ChannelContextService.RenderChannelInfo(null, 1));
    }

    [Fact]
    public void RenderParticipants_ListsEachWithTrustAndMemo()
    {
        var ps = new List<ParticipantView>
        {
            new("张三", "三儿", "111", 5, 2, "老熟人"),
            new("李四", null, "222", 6, 0, null),
        };
        var s = ChannelContextService.RenderParticipants("group_42", 9, ps);
        Assert.Contains("[当前参与者]", s);
        Assert.Contains("张三", s);
        Assert.Contains("别称:三儿", s);
        Assert.Contains("platform_id:111", s);
        Assert.Contains("person_id:5", s);
        Assert.Contains("trust:2", s);
        Assert.Contains("老熟人", s);
        Assert.Contains("李四", s);
    }

    [Fact]
    public void RenderParticipants_Empty_ReturnsEmpty()
    {
        Assert.Equal("", ChannelContextService.RenderParticipants("group_1", 1, new List<ParticipantView>()));
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test AgentCoreProcessor.Tests --filter ChannelContextServiceTests`
Expected: 编译失败（ChannelContextService/ParticipantView 未定义）

- [ ] **Step 3: 实现 service 纯方法**

迁移自 `ChannelContextBuilder.cs:73-86`（FormatChannelContext）和 `:351-377`（参与者渲染）的逻辑，改成纯静态、读参数不读字段：

```csharp
// AgentCoreProcessor/Engine/Context/ChannelContextService.cs
using System.Collections.Generic;
using System.Text;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>频道上下文的一个参与者视图（DB 派生，无累积状态）。</summary>
internal record ParticipantView(
    string DisplayName, string? Aliases, string PlatformId,
    int PersonId, int TrustLevel, string? Memo);

/// <summary>
/// 频道上下文构建服务。ChannelEngine 与 RuntimeSession 共用。
/// 纯格式化方法静态；依赖 DB 的方法在后续 Task 加入（实例方法，注入 ISystemContext）。
/// </summary>
internal partial class ChannelContextService
{
    /// <summary>渲染频道信息行（群号/私聊对象/频道ID）。channelName 格式 "group_xxx"/"private_xxx"。</summary>
    public static string RenderChannelInfo(string? channelName, int channelId)
    {
        if (string.IsNullOrEmpty(channelName)) return "";
        var parts = channelName.Split('_', 2);
        if (parts.Length != 2) return "";
        var id = parts[1];
        return parts[0] switch
        {
            "group" => $"当前频道：群聊，群号: {id}，频道ID: {channelId}。对群成员操作时需提供 group_id={id}。",
            "private" => $"当前频道：私聊，对方QQ: {id}，频道ID: {channelId}。",
            _ => ""
        };
    }

    /// <summary>渲染参与者列表块。空列表返回空串。</summary>
    public static string RenderParticipants(
        string? channelName, int channelId, IReadOnlyList<ParticipantView> participants)
    {
        if (participants.Count == 0) return "";
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(channelName))
        {
            var p = channelName.Split('_', 2);
            if (p.Length == 2)
                sb.AppendLine($"[频道] 类型: {p[0]}, 平台ID: {p[1]}, 频道ID: {channelId}");
        }
        sb.AppendLine("[当前参与者]");
        foreach (var p in participants)
        {
            sb.Append($"- {p.DisplayName}");
            if (!string.IsNullOrEmpty(p.Aliases)) sb.Append($" (别称:{p.Aliases})");
            sb.Append($" [platform_id:{p.PlatformId}, person_id:{p.PersonId}, trust:{p.TrustLevel}]");
            if (!string.IsNullOrEmpty(p.Memo)) sb.Append($" — {p.Memo}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `dotnet test AgentCoreProcessor.Tests --filter ChannelContextServiceTests`
Expected: Passed! - Failed: 0, Passed: 5

- [ ] **Step 5: Commit**

```bash
git add AgentCoreProcessor/Engine/Context/ChannelContextService.cs AgentCoreProcessor.Tests/ChannelContextServiceTests.cs
git commit -m "feat: add ChannelContextService pure formatters with tests"
```

### Task 3：参与者 DB 派生 — GetParticipantsAsync

把参与者从"累积状态"变成"DB 查询"。为可测，给 service 一个**窄数据接缝** `IChannelDataSource`，生产实现包 `ISystemContext.Session`，测试用假实现。

**Files:**
- Modify: `AgentCoreProcessor/Engine/Context/ChannelContextService.cs`
- Create: `AgentCoreProcessor/Engine/Context/IChannelDataSource.cs`
- Modify: `AgentCoreProcessor.Tests/ChannelContextServiceTests.cs`

**Interfaces:**
- Consumes: `ParticipantView`（Task 2）
- Produces:
  - `interface IChannelDataSource`，含 `Task<List<UserMessage>> GetRecentMessagesAsync(int channelId, int limit)` / `Task<User?> GetUserByIdAsync(int userId)` / `Task<Person?> GetPersonByIdAsync(int personId)`
  - 实例方法 `Task<List<ParticipantView>> ChannelContextService.GetParticipantsAsync(int channelId, int limit = 30)`
  - 构造 `ChannelContextService(IChannelDataSource data)`

- [ ] **Step 1: 数据接缝接口**

```csharp
// AgentCoreProcessor/Engine/Context/IChannelDataSource.cs
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Engine.Context;

/// <summary>ChannelContextService 的窄数据访问接缝，便于单测注入假数据。</summary>
internal interface IChannelDataSource
{
    Task<List<UserMessage>> GetRecentMessagesAsync(int channelId, int limit);
    Task<User?> GetUserByIdAsync(int userId);
    Task<Person?> GetPersonByIdAsync(int personId);
}
```

- [ ] **Step 2: 写失败测试（假数据源）**

```csharp
// 追加到 ChannelContextServiceTests.cs
using AgentCoreProcessor.Database;

file class FakeDataSource : IChannelDataSource
{
    public List<UserMessage> Messages = new();
    public Dictionary<int, User> Users = new();
    public Dictionary<int, Person> Persons = new();
    public Task<List<UserMessage>> GetRecentMessagesAsync(int channelId, int limit)
        => Task.FromResult(Messages.FindAll(m => m.ChannelId == channelId));
    public Task<User?> GetUserByIdAsync(int userId)
        => Task.FromResult(Users.TryGetValue(userId, out var u) ? u : null);
    public Task<Person?> GetPersonByIdAsync(int personId)
        => Task.FromResult(Persons.TryGetValue(personId, out var p) ? p : null);
}

public class ParticipantDerivationTests
{
    [Fact]
    public async Task GetParticipants_DistinctSendersFromRecentMessages()
    {
        var data = new FakeDataSource();
        data.Messages.Add(new UserMessage { ChannelId = 1, UserId = 10, IsFromBot = false });
        data.Messages.Add(new UserMessage { ChannelId = 1, UserId = 10, IsFromBot = false }); // 重复发送者
        data.Messages.Add(new UserMessage { ChannelId = 1, UserId = 20, IsFromBot = false });
        data.Messages.Add(new UserMessage { ChannelId = 1, UserId = 0,  IsFromBot = true });  // bot 跳过
        data.Users[10] = new User { Id = 10, PersonId = 100, PlatformId = "p10", DisplayName = "甲" };
        data.Users[20] = new User { Id = 20, PersonId = 200, PlatformId = "p20", DisplayName = "乙" };
        data.Persons[100] = new Person { Id = 100, Name = "甲名", TrustLevel = 2 };
        data.Persons[200] = new Person { Id = 200, Name = "乙名", TrustLevel = 1 };

        var svc = new ChannelContextService(data);
        var ps = await svc.GetParticipantsAsync(1);

        Assert.Equal(2, ps.Count);                       // distinct，bot 排除
        Assert.Contains(ps, p => p.PlatformId == "p10" && p.DisplayName == "甲名" && p.TrustLevel == 2);
        Assert.Contains(ps, p => p.PlatformId == "p20" && p.DisplayName == "乙名");
    }
}
```

- [ ] **Step 3: 运行确认失败**

Run: `dotnet test AgentCoreProcessor.Tests --filter ParticipantDerivationTests`
Expected: 编译失败（构造函数/GetParticipantsAsync 未定义）

- [ ] **Step 4: 实现（逻辑迁移自 ChannelAccessImpl.BuildParticipantMapAsync）**

在 `ChannelContextService` 加实例字段、构造、方法。DisplayName 优先级 person.Name → user.DisplayName → user.PlatformId：

```csharp
// 在 ChannelContextService 类内补充
private readonly IChannelDataSource _data;
public ChannelContextService(IChannelDataSource data) => _data = data;

/// <summary>从频道最近消息派生当前参与者（distinct 发送者，排除 bot）。无累积状态。</summary>
public async Task<List<ParticipantView>> GetParticipantsAsync(int channelId, int limit = 30)
{
    var result = new List<ParticipantView>();
    var seen = new HashSet<int>();
    var msgs = await _data.GetRecentMessagesAsync(channelId, limit);
    foreach (var m in msgs)
    {
        if (m.IsFromBot || m.UserId <= 0 || !seen.Add(m.UserId)) continue;
        var user = await _data.GetUserByIdAsync(m.UserId);
        if (user == null) continue;
        var person = await _data.GetPersonByIdAsync(user.PersonId);
        var displayName = !string.IsNullOrEmpty(person?.Name) ? person!.Name
            : !string.IsNullOrEmpty(user.DisplayName) ? user.DisplayName!
            : user.PlatformId;
        result.Add(new ParticipantView(
            displayName, person?.Aliases, user.PlatformId,
            user.PersonId, person?.TrustLevel ?? 0, person?.FastMemory));
    }
    return result;
}
```
> 执行时确认 `User`/`Person` 实体的真实属性名（`DisplayName`/`Aliases`/`TrustLevel`/`FastMemory`），按实际调整。本计划依据 architecture-map 的用户系统记录。

- [ ] **Step 5: 运行确认通过**

Run: `dotnet test AgentCoreProcessor.Tests --filter ParticipantDerivationTests`
Expected: Passed! - Failed: 0, Passed: 1

- [ ] **Step 6: Commit**

```bash
git add AgentCoreProcessor/Engine/Context/IChannelDataSource.cs AgentCoreProcessor/Engine/Context/ChannelContextService.cs AgentCoreProcessor.Tests/ChannelContextServiceTests.cs
git commit -m "feat: derive participants from DB query, not accumulated state"
```

### Task 4：迁移历史/引用展开/图片交错到 service

把 `ChannelContextBuilder.cs` 里复杂的格式化件搬进 service，改成读 `ChannelContextState` 而非 ChannelEngine 字段。这些方法逻辑复杂（200+ 行）且依赖 ImageStorage/DB，**迁移而非重写**：保持方法体逻辑不变，只替换状态来源。

**Files:**
- Modify: `AgentCoreProcessor/Engine/Context/ChannelContextService.cs`
- Modify: `AgentCoreProcessor/Engine/Context/IChannelDataSource.cs`（按需补查询方法）

**Interfaces:**
- Consumes: `ChannelContextState`, `IChannelDataSource`
- Produces（实例方法，签名供 Task 5/Task 8 消费）：
  - `Task<List<Message>> BuildHistoryBlockAsync(ChannelContextState st)` — 历史块（含 quoted-context + 图片交错）
  - `Task<Message?> FormatIncomingAsync(ChannelContextState st, UserMessage m)` — 单条新消息 → `<new_messages>` 格式 Message（含 mention 前缀、reply 补块、附件 ContentParts）
  - `Task<List<Message>> BuildLaggingImageInjectionsAsync(ChannelContextState st)` — 滞后图片描述/OCR 补注

- [ ] **Step 1: 迁移方法**

从 `ChannelContextBuilder.cs` 迁入以下方法，逐一把字段引用替换为 `st.` 成员：
- `BuildQuotedContextForBatchAsync` / `AppendQuotedContextRecursiveAsync` / `CollectImagePaths`（静态，无字段依赖，直接搬）
- `AddQuotedContextMessage` / `BuildInterleavedContentParts` / `BuildContentPartsWithImagePaths`
  - `_roundImageHashes` → `st.RoundImageHashes`
  - `_injectedDescriptions` → `st.InjectedDescriptions`
  - `_injectedOcrTexts` → `st.InjectedOcrTexts`
- 历史拉取段（`ChannelContextBuilder.cs:402-435`）整理为 `BuildHistoryBlockAsync(st)`：
  - `_lastConsumedMessageId` → `st.LastConsumedMessageId`，`_startInjectMaxId` → `st.StartInjectMaxId`
  - `ctx.Session.GetContextByChannelAsync` → `_data.GetRecentMessagesAsync`
  - `IsBotMentionedInMessage` / `GetBotPlatformId` 也迁入（bot platform id 通过 `IChannelDataSource` 暴露或构造注入）
- 新消息格式化段（`BuildRoundInjectAsync` 内 `NewMessageSignal` 分支 + DB 追赶段）整理为 `FormatIncomingAsync(st, m)`
- 滞后补注段（`:638-660`）→ `BuildLaggingImageInjectionsAsync(st)`

在 `IChannelDataSource` 按需补 `GetByPlatformMessageIdAsync` / `GetContextAroundAsync` / `GetBotPlatformId` 等被引用的查询，生产实现转发给 `ISystemContext.Session` / `ctx.Adapters`。

- [ ] **Step 2: 编译验证**

Run: `dotnet build AgentCoreProcessor -clp:ErrorsOnly`
Expected: 0 错误（service 自洽，ChannelEngine 尚未切换，旧方法暂时并存）

- [ ] **Step 3: 引用展开纯逻辑测试（可选但推荐）**

对 `FormatIncomingAsync` 的 mention 前缀和 reply 标记加一个用假 `IChannelDataSource` 的测试，断言输出含 `[@你]` / `<new_messages>` / `reply="..."`。

Run: `dotnet test AgentCoreProcessor.Tests --filter FormatIncoming`
Expected: Passed

- [ ] **Step 4: Commit**

```bash
git add AgentCoreProcessor/Engine/Context/
git commit -m "feat: migrate history/quoted-context/image formatting into ChannelContextService"
```

---

## Phase 2：ChannelEngine 委托 service（Express 零行为变化）

### Task 5：ChannelEngine 改用 ChannelContextService

**Files:**
- Modify: `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs`（先成功 Read 确认行号）
- Modify: `AgentCoreProcessor/Engine/Worker/ChannelContextBuilder.cs`

**Interfaces:**
- Consumes: Task 1-4 的 `ChannelContextState` + `ChannelContextService` 全部方法

- [ ] **Step 1: ChannelEngine 持有 state + service**

在 ChannelEngine 加字段：`private ChannelContextState _ctxState;` 和 `private ChannelContextService _ctxService;`，在初始化（ComponentHost 初始化附近）构造：`_ctxState = new() { ChannelId = channelId, ChannelName = channelName, Mode = "express" }`，`_ctxService = new(new SystemContextDataSource(ctx))`（生产数据源适配器，转发 `ctx.Session`）。

- [ ] **Step 2: BuildStartInjectAsync 内部委托**

把 `FormatChannelContext()`→`ChannelContextService.RenderChannelInfo(channelName, channelId)`；参与者块→`RenderParticipants(channelName, channelId, await _ctxService.GetParticipantsAsync(channelId))`；历史块→`await _ctxService.BuildHistoryBlockAsync(_ctxState)`。保持方法签名 `BuildStartInjectAsync()` 不变（IAgentHost 契约）。同步游标：调用后把 `_lastConsumedMessageId = _ctxState.LastConsumedMessageId`。

- [ ] **Step 3: BuildRoundInjectAsync 内部委托**

新消息格式化与滞后补注改调 `_ctxService.FormatIncomingAsync` / `BuildLaggingImageInjectionsAsync`，共享 `_ctxState`。

- [ ] **Step 4: 删除 recentParticipants 累积**

移除 `recentParticipants` 字段、`ParticipantInfo.From` 的 AddOrUpdate 调用（`ChannelEngine.cs:201,287-290`）、`currentParticipantSnapshot`（`:136,483`）。`ChannelExtractionWorker` 若依赖 participants 参数，改为接收 `_ctxService.GetParticipantsAsync` 结果或传 channelId 自取。`ParticipantInfo.cs`/`BotOutputParser.cs` 若不再被引用则一并清理（确认无其他引用后删）。

- [ ] **Step 5: 编译**

Run: `cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null; dotnet build -clp:ErrorsOnly`
Expected: 0 错误

- [ ] **Step 6: 集成验证 — Express 不退化**

Run: `dotnet run --project AgentCoreProcessor -- --test --delay 2`
观察日志："上下文组装完成" 事件仍出现，participant/历史/频道信息注入内容与改造前一致，Express 能正常回话。
Expected: Express 行为与改造前无差异

- [ ] **Step 7: Commit**

```bash
git add AgentCoreProcessor/Engine/Worker/
git commit -m "refactor: ChannelEngine delegates context building to ChannelContextService"
```

---

## Phase 3：WorkingCore → SessionCore 改名 + spec 字段

### Task 6：Core 配置改名 + CreateSessionSpec/PortSpec 扩展

**Files:**
- Rename: `Storage/Core/WorkingCore.json` → `Storage/Core/SessionCore.json`（Storage 非 git，用文件操作）
- Modify: `AgentCoreProcessor/Core/AgentCore.cs`, `AgentCoreProcessor/Command/ConfigCommand.cs`, `AgentCoreProcessor/WebUI/Providers/ConfigProvider.cs`, `AgentCoreProcessor/WebUI/Providers/TestProvider.cs`
- Modify: `AgentLilara.PluginSDK/CreateSessionSpec.cs`, `AgentLilara.PluginSDK/PortSpec.cs`

**Interfaces:**
- Produces: `CreateSessionSpec.CoreConfigName`（默认 `"SessionCore"`）、`PortSpec.PreloadHistory`（默认 `true`）

- [ ] **Step 1: 改 Storage 配置文件名**

Run: `cd E:/Workspace/AgentLilaraProject && mv Storage/Core/WorkingCore.json Storage/Core/SessionCore.json`
Expected: 文件改名成功

- [ ] **Step 2: AgentCore.cs 全量替换 WorkingCore**

`AgentCore.cs:18` `currentMode = "SessionCore"`；`:36` `base("SessionCore")`；`:50` `mode == Express ? "ExpressCore" : "SessionCore"`；`:13` 注释更新。

- [ ] **Step 3: ConfigCommand + ConfigProvider 映射改名**

`ConfigCommand.cs:30` `Path.Combine(..., "SessionCore.json")`；`ConfigProvider.cs:19` `["SessionCore"] = "Core/SessionCore.json"`（连键名一起改）；`TestProvider.cs:374-375` mock 文案 `[SessionCore]`。

- [ ] **Step 4: SDK 字段**

`CreateSessionSpec.cs` 加 `public string CoreConfigName { get; set; } = "SessionCore";`
`PortSpec.cs` 加 `public bool PreloadHistory { get; set; } = true;`

- [ ] **Step 5: 编译 + 启动验证配置可加载**

Run: `cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null; dotnet build -clp:ErrorsOnly && dotnet run --project AgentCoreProcessor -- --test --delay 2`
Expected: 0 错误；启动无 "SessionCore.json not found"；Express 正常（ExpressCore 不受影响）

- [ ] **Step 6: Commit**

```bash
git add AgentCoreProcessor/Core/AgentCore.cs AgentCoreProcessor/Command/ConfigCommand.cs AgentCoreProcessor/WebUI/Providers/ConfigProvider.cs AgentCoreProcessor/WebUI/Providers/TestProvider.cs AgentLilara.PluginSDK/CreateSessionSpec.cs AgentLilara.PluginSDK/PortSpec.cs
git commit -m "refactor: rename WorkingCore to SessionCore, add CoreConfigName/PreloadHistory"
```

---

## Phase 4：SessionAgentHost + 统一 session 初始化

### Task 7：SessionAgentHost — session 的上下文宿主

**Files:**
- Create: `AgentCoreProcessor/Engine/Session/SessionAgentHost.cs`

**Interfaces:**
- Consumes: `ChannelContextService`, `ChannelContextState`（Phase 1）；`IRuntimeSession` 的端口
- Produces:
  - `class SessionAgentHost`，构造 `(IReadOnlyList<(int channelId, string? channelName, bool preload)> channelPorts, string? initialState, ChannelContextService svc)`
  - `Task<List<Message>> BuildStartAsync()` — 每个 preload 频道端口的上下文块（频道信息+参与者+历史）+ InitialState
  - `Task<List<Message>> BuildRoundAsync(IEnumerable<UserMessage> drained)` — 当前时间 + 每条 drained 走 FormatIncoming + 滞后补注
  - `Message FormatToolResults(...)` / `Message FormatAssistant(...)` — 复用 Express 的 tool_use/tool_result 配对形态（Phase 5/第二批用，此处先放占位声明，实现留第二批）

> 说明：本 Task 只实现 `BuildStartAsync`/`BuildRoundAsync`（上下文组装）。tool_use 配对属于"接通管道"，留第二批；此处不写其实现，避免与第二批冲突。

- [ ] **Step 1: 实现 host**

```csharp
// AgentCoreProcessor/Engine/Session/SessionAgentHost.cs
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// RuntimeSession 的上下文宿主。session 的认知 = 挂载的频道端口集合（每根贡献
/// 频道信息+参与者+历史）+ InitialState。working/task 共用，差异只在传入的端口集合。
/// </summary>
internal class SessionAgentHost
{
    private readonly IReadOnlyList<(int channelId, string? channelName, bool preload)> _channelPorts;
    private readonly string? _initialState;
    private readonly ChannelContextService _svc;
    private readonly Dictionary<int, ChannelContextState> _states = new();

    public SessionAgentHost(
        IReadOnlyList<(int channelId, string? channelName, bool preload)> channelPorts,
        string? initialState, ChannelContextService svc)
    {
        _channelPorts = channelPorts;
        _initialState = initialState;
        _svc = svc;
        foreach (var (cid, cname, _) in channelPorts)
            _states[cid] = new ChannelContextState { ChannelId = cid, ChannelName = cname, Mode = "session" };
    }

    public ChannelContextState StateFor(int channelId) => _states[channelId];

    /// <summary>起始上下文：每个 preload 频道端口的上下文块 + InitialState 当前指令。</summary>
    public async Task<List<Message>> BuildStartAsync()
    {
        var msgs = new List<Message>();
        foreach (var (cid, cname, preload) in _channelPorts)
        {
            if (!preload) continue;
            var st = _states[cid];
            var info = ChannelContextService.RenderChannelInfo(cname, cid);
            if (!string.IsNullOrEmpty(info))
                msgs.Add(new Message { Role = "user", Content = info });
            var parts = ChannelContextService.RenderParticipants(cname, cid, await _svc.GetParticipantsAsync(cid));
            if (!string.IsNullOrEmpty(parts))
                msgs.Add(new Message { Role = "user", Content = parts });
            msgs.AddRange(await _svc.BuildHistoryBlockAsync(st));
        }
        if (!string.IsNullOrEmpty(_initialState))
            msgs.Add(new Message { Role = "user", Content = $"[当前任务]\n{_initialState}" });
        return msgs;
    }

    /// <summary>每轮：当前时间 + drained 新消息格式化 + 滞后图片补注。</summary>
    public async Task<List<Message>> BuildRoundAsync(IEnumerable<(int channelId, UserMessage msg)> drained)
    {
        var msgs = new List<Message>
        {
            new() { Role = "user", Content = $"[系统] 当前时间: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss dddd} UTC+8 (Asia/Shanghai)" }
        };
        foreach (var (cid, m) in drained)
        {
            var formatted = await _svc.FormatIncomingAsync(_states[cid], m);
            if (formatted != null) msgs.Add(formatted);
        }
        foreach (var (cid, _, _) in _channelPorts)
            msgs.AddRange(await _svc.BuildLaggingImageInjectionsAsync(_states[cid]));
        return msgs;
    }
}
```

- [ ] **Step 2: 编译**

Run: `dotnet build AgentCoreProcessor -clp:ErrorsOnly`
Expected: 0 错误

- [ ] **Step 3: Commit**

```bash
git add AgentCoreProcessor/Engine/Session/SessionAgentHost.cs
git commit -m "feat: add SessionAgentHost for unified port-driven session context"
```

### Task 8：RuntimeSession/SessionEngine 接入 SessionAgentHost

**Files:**
- Modify: `AgentCoreProcessor/Engine/Session/RuntimeSession.cs`
- Modify: `AgentCoreProcessor/Engine/Session/SessionEngine.cs`

**Interfaces:**
- Consumes: `SessionAgentHost`（Task 7）、`CreateSessionSpec.CoreConfigName` / `PortSpec.PreloadHistory`（Task 6）

- [ ] **Step 1: SessionEngine 传正确 loopId + Core 名**

`CreateSession` 中：单频道端口时 ComponentHost loopId 用 `LoopId.ForChannel(channelId)`（让 SpeakTool 解析到真实 channelId），多/零端口用 `session:{id}`；`new AgentCore(spec.CoreConfigName)` 替代裸 `new AgentCore()`。构造 `ChannelContextService` 并据 spec.InputPorts + 各 PortSpec.PreloadHistory 组装 `channelPorts` 列表，传给 session。

- [ ] **Step 2: RuntimeSession 用 host 构建上下文**

`RunAsync` 中：起始 `messages = await _host.BuildStartAsync()` 替代裸 InitialState 字符串；每轮 drain 后 `messages.AddRange(await _host.BuildRoundAsync(drained))`。channelName 通过 `IChannelDataSource`/`ISystemContext.Session` 在 CreateSession 时查好传入。

> 注：本 Task 只接通"上下文来源"。tool_use 配对、出向路由、入向接通仍是第二批；此处 session 仍可能发不出话，但**上下文内容已正确组装**——通过日志可验证。

- [ ] **Step 3: 编译**

Run: `cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null; dotnet build -clp:ErrorsOnly`
Expected: 0 错误

- [ ] **Step 4: 集成验证 — escalate 产出富上下文**

Run: `dotnet run --project AgentCoreProcessor -- --test --delay 2`，在 Console 触发一次需要 escalate 的对话。
观察日志：session 创建后，模型输入含 [频道信息]+[当前参与者]+[对话历史]+[当前任务]，而非裸历史 dump。Express 路径仍正常。
Expected: session 上下文内容完整；Express 不退化

- [ ] **Step 5: Commit**

```bash
git add AgentCoreProcessor/Engine/Session/RuntimeSession.cs AgentCoreProcessor/Engine/Session/SessionEngine.cs
git commit -m "feat: RuntimeSession builds context via SessionAgentHost, correct loopId for speak"
```

---

## Phase 5：文档收尾

### Task 9：更新架构文档

**Files:**
- Modify: `docs/architecture-map.md`, `AgentCoreProcessor/CLAUDE.md`, `docs/core/configuration.md`, `docs/core/agent-core.md`

- [ ] **Step 1: 文档替换 WorkingCore → SessionCore**

`docs/core/configuration.md`、`docs/core/agent-core.md` 中 WorkingCore 引用改名；说明它现在专供 RuntimeSession，不再是"Working 模式"。

- [ ] **Step 2: architecture-map 增补 ChannelContextService**

在"引擎生态"或新增"上下文机器"小节，记录：ChannelContextService 为 ChannelEngine 与 RuntimeSession 共用，参与者 DB 派生（不再累积），session 认知 = 频道端口集合 + InitialState。

- [ ] **Step 3: CLAUDE.md 关键路径增补**

加 `Engine/Context/ChannelContextService.cs` + `ChannelContextState.cs` + `Session/SessionAgentHost.cs` 路径条目；标注 WorkingCore→SessionCore 改名。

- [ ] **Step 4: Commit**

```bash
git add docs/ AgentCoreProcessor/CLAUDE.md
git commit -m "docs: document ChannelContextService and SessionCore rename"
```

---

## 第二批预告（不在本计划执行范围）

本计划只立地基（上下文/提示词机器）。**完成后** session 仍发不出话，因为下列"接通管道"属于第二批：

1. **入向接通**：AdapterEngine → SessionManager 解析身份 → MessageEngine.AcceptIncoming 入库 → 分发订阅者（含被占用频道路由到 session 端口）
2. **出向接通**：MessageEngine.OnOutgoingReady 订阅 → AdapterEngine.SendOutgoing；AdapterEngine.RegisterAdapter 接线
3. **tool_use/tool_result 配对**：RuntimeSession agent 循环复用 Express 的 Format 方法（修孤儿 tool_result）
4. **循环正确性**：计费/轮次（B1-B2）、deescalate 注册（B4）、事件驱动 drain
5. **端口激活权重**、ScheduledTask/TODO 端口与持久化（C3-C4）

地基立住后，第二批每一项都有明确接入点。

---

## Self-Review

**Spec coverage（对照本轮讨论敲定的设计）：**
- ✅ 抽 ChannelContextService 两层共用 → Phase 1 + Task 5
- ✅ 参与者降级为 DB 派生 → Task 3 + Task 5 Step 4
- ✅ WorkingCore 改名 SessionCore + 保留 forceToolCall → Task 6
- ✅ 端口挂载触发上下文（非初始化时机）→ Task 7（preload 标志 + BuildStartAsync 遍历端口）
- ✅ 统一 working/task 初始化，差异在 spec → Task 7/8（channelPorts 列表驱动）
- ✅ InitialState = 当前指令，排在上下文块后 → Task 7 BuildStartAsync
- ✅ speak channelId 修正（顺带）→ Task 8 Step 1（单频道端口用 LoopId.ForChannel）
- ✅ 单元测试纳入 → Phase 0 + Task 2/3 测试步骤

**已知留待执行时确认（非占位，是工具异常/实体属性的现场核对）：**
- ChannelEngine.cs 行号：执行时先 Read 确认（已在 Global/Task 5 标注）
- User/Person 实体真实属性名（DisplayName/Aliases/TrustLevel/FastMemory）：Task 3 Step 4 已标注按实际调整
- IChannelDataSource 最终方法集：Task 4 按迁移时实际引用补全

**Type consistency：**
- `ChannelContextState` 字段在 Task 1 定义，Task 2/3/4/7 一致引用
- `ParticipantView` 在 Task 2 定义，Task 3 GetParticipantsAsync 返回、Task 7 RenderParticipants 消费，签名一致
- `ChannelContextService` 构造 `(IChannelDataSource)` 在 Task 3 定义，Task 5/7/8 一致使用
- `CoreConfigName`/`PreloadHistory` 在 Task 6 定义，Task 8 消费

> 注：tool_use 配对的 `FormatToolResults`/`FormatAssistant` 在 Task 7 仅声明意图、实现留第二批，避免与第二批的 agent 循环改造冲突——这是有意的边界，非缺漏。

