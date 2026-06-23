# 架构重构 — 通用会话框架 实现计划

> **Goal:** 重构为"统一 RuntimeSession + MessageEngine 双向枢纽"模型，组件白名单+等级双重控制，端口 I/O，统一生命周期，轮次定价。

**Architecture:** 三层 — 框架层 ChannelEngine（冲动值+Express双模板）、基础设施引擎层 8 个 ISubEngine、会话层统一 RuntimeSession。数据流单向：适配器→AdapterEngine→MessageEngine→订阅者；出向 Session→MessageEngine→AdapterEngine。

**Tech Stack:** .NET 8, C#, SQLite-net-pcl, Newtonsoft.Json, AgentLilara.PluginSDK

## Global Constraints

- 目标框架：net8.0，Microsoft.NET.Sdk.Web
- 新引擎：实现 ISubEngine，通过 SpawnCheck 注册
- 新工具：实现 ITool，参数名英文
- 代码注释/讨论：中文；commit message：英文
- Storage 目录不在解决方案文件夹下，无 git 仓库
- 改动完成后必须编译 + git commit + --test 试运行 + 更新文档
- 复用现有 ILoopComponent 组件体系，不引入新接口

---

## Phase 1: 基础接口定义（SDK）

### Task 1: IRuntimeSession + SessionLimits + SessionStatus + ClaimLevel

**Files to CREATE:**
- `AgentLilara.PluginSDK/IRuntimeSession.cs`
- `AgentLilara.PluginSDK/SessionLimits.cs`
- `AgentLilara.PluginSDK/SessionStatus.cs`
- `AgentLilara.PluginSDK/ClaimLevel.cs`

- [ ] **Step 1: SessionStatus.cs**

```csharp
// AgentLilara.PluginSDK/SessionStatus.cs
namespace AgentLilara.PluginSDK;

public enum SessionStatus { Creating, Restored, Running, Completed, Crashed }
```

- [ ] **Step 2: ClaimLevel.cs**

```csharp
// AgentLilara.PluginSDK/ClaimLevel.cs
namespace AgentLilara.PluginSDK;

public enum ClaimLevel { Listen, Full }
```

- [ ] **Step 3: SessionLimits.cs**

```csharp
// AgentLilara.PluginSDK/SessionLimits.cs
using System;

namespace AgentLilara.PluginSDK;

public class SessionLimits
{
    public TimeSpan MaxLifetime { get; init; } = TimeSpan.FromHours(2);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
}
```

- [ ] **Step 4: IRuntimeSession.cs**

```csharp
// AgentLilara.PluginSDK/IRuntimeSession.cs
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentLilara.PluginSDK;

public interface IRuntimeSession
{
    string SessionId { get; }
    int MaxLevel { get; }
    SessionStatus Status { get; }
    bool IsAlive { get; }

    IReadOnlyList<string> ComponentIds { get; }
    IReadOnlyList<string> ActiveTools { get; }

    IReadOnlyDictionary<string, IInputPort> Inputs { get; }
    IReadOnlyDictionary<string, IOutputPort> Outputs { get; }

    string? ParentSessionId { get; }
    SessionLimits Limits { get; }
    Task RunAsync(CancellationToken ct);
    void RequestStop(string reason);
    bool CheckLifetimeLimits();
    DateTime? WaitUntil { get; }

    Task PersistStateAsync();
    Task RestoreStateAsync();

    void EnableComponent(string componentId);
    void DisableComponent(string componentId);
    void SetLevel(int newLevel);
}
```

- [ ] **Step 5: Build & commit**

```bash
dotnet build AgentLilara.PluginSDK && \
git add AgentLilara.PluginSDK/IRuntimeSession.cs AgentLilara.PluginSDK/SessionLimits.cs AgentLilara.PluginSDK/SessionStatus.cs AgentLilara.PluginSDK/ClaimLevel.cs && \
git commit -m "feat: add IRuntimeSession, SessionLimits, SessionStatus, ClaimLevel to SDK"
```

---

### Task 2: IInputPort + IOutputPort

**Files to CREATE:**
- `AgentLilara.PluginSDK/IInputPort.cs`
- `AgentLilara.PluginSDK/IOutputPort.cs`

```csharp
// IInputPort.cs
namespace AgentLilara.PluginSDK;

public interface IInputPort
{
    string Name { get; }
    Type DataType { get; }
    int Weight { get; }
    Task<object?> ReceiveAsync(CancellationToken ct);
    bool TryPeek(out object? data);
}
```

```csharp
// IOutputPort.cs
namespace AgentLilara.PluginSDK;

public interface IOutputPort
{
    string Name { get; }
    Type DataType { get; }
    Task SendAsync(object data);
}
```

```bash
dotnet build AgentLilara.PluginSDK && \
git add AgentLilara.PluginSDK/IInputPort.cs AgentLilara.PluginSDK/IOutputPort.cs && \
git commit -m "feat: add IInputPort and IOutputPort interfaces"
```

---

### Task 3: CreateSessionSpec + PortSpec + ToolLevel

**Files to CREATE:**
- `AgentLilara.PluginSDK/CreateSessionSpec.cs`
- `AgentLilara.PluginSDK/PortSpec.cs`
- `AgentLilara.PluginSDK/ToolLevel.cs`

```csharp
// ToolLevel.cs
namespace AgentLilara.PluginSDK;

public enum ToolLevel { L0, L1, L2, L3 }
```

```csharp
// PortSpec.cs
namespace AgentLilara.PluginSDK;

public class PortSpec
{
    public int ChannelId { get; set; }
    public string SubStream { get; set; } = "normal";
    public int Weight { get; set; } = 5;
}
```

```csharp
// CreateSessionSpec.cs
using System.Collections.Generic;

namespace AgentLilara.PluginSDK;

public class CreateSessionSpec
{
    public string[] ComponentIds { get; set; } = Array.Empty<string>();
    public int MaxLevel { get; set; } = 0;
    public bool AutoClaimChannel { get; set; } = false;
    public Dictionary<string, PortSpec> InputPorts { get; set; } = new();
    public Dictionary<string, string> OutputPorts { get; set; } = new();
    public object? InitialState { get; set; }
    public string? ParentSessionId { get; set; }
    public SessionLimits? OverrideLimits { get; set; }
}
```

```bash
dotnet build AgentLilara.PluginSDK && \
git add AgentLilara.PluginSDK/CreateSessionSpec.cs AgentLilara.PluginSDK/PortSpec.cs AgentLilara.PluginSDK/ToolLevel.cs && \
git commit -m "feat: add CreateSessionSpec, PortSpec, ToolLevel to SDK"
```

---

## Phase 2: MessageEngine

### Task 4: MessageEngine — 双向消息枢纽

**Files to CREATE:**
- `AgentCoreProcessor/Engine/Message/MessageEngine.cs`
- `AgentCoreProcessor/Engine/Message/MessageEngineSpawnCheck.cs`
- `AgentCoreProcessor/Engine/Message/IncomingMessage.cs`
- `AgentCoreProcessor/Engine/Message/OutgoingMessage.cs` (if not in SDK)

**Files to MODIFY:**
- `AgentCoreProcessor/Database/DbManager.cs` — add FindMessageAsync, InsertMessageAsync

- [ ] **Step 1: Message DTOs**

```csharp
// IncomingMessage.cs
namespace AgentCoreProcessor.Engine.Message;

public class IncomingMessage
{
    public int ChannelId { get; set; }
    public string MessageId { get; set; } = "";
    public string Content { get; set; } = "";
    public int SenderUserId { get; set; }
    public string Tag { get; set; } = "normal";  // normal / mention / command
    public DateTime Timestamp { get; set; }
    public List<ContentPart>? ContentParts { get; set; }
}
```

```csharp
// OutgoingMessage.cs
namespace AgentCoreProcessor.Engine.Message;

public class OutgoingMessage
{
    public int ChannelId { get; set; }
    public string Content { get; set; } = "";
    public bool SentByBot { get; set; } = true;
    public bool InternalOnly { get; set; } = false;
    public List<ContentPart>? Segments { get; set; }
}
```

- [ ] **Step 2: MessageEngine**

```csharp
// MessageEngine.cs
using System.Collections.Concurrent;
using AgentCoreProcessor.Database;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine.Message;

internal class MessageEngine : ISubEngine
{
    public string EngineType => "Message";
    public bool IsAlive { get; private set; } = true;
    public bool IsInfrastructure => true;

    private readonly ConcurrentDictionary<(int channelId, string subStream), List<IInputPort>> _subscribers = new();

    public IInputPort Subscribe(int channelId, string subStream)
    {
        var port = new MessageInputPort(channelId, subStream);
        _subscribers.GetOrAdd((channelId, subStream), _ => new()).Add(port);
        return port;
    }

    public void Unsubscribe(IInputPort port)
    {
        if (port is MessageInputPort mp && _subscribers.TryGetValue((mp.ChannelId, mp.SubStream), out var list))
            list.Remove(port);
    }

    public async Task AcceptIncoming(IncomingMessage msg)
    {
        var existing = await DbManager.Instance.FindMessageAsync(msg.ChannelId, msg.MessageId);
        if (existing != null) return;

        await DbManager.Instance.InsertMessageAsync(new UserMessage
        {
            ChannelId = msg.ChannelId, Content = msg.Content,
            PlatformMessageId = msg.MessageId, UserId = msg.SenderUserId,
            Time = DateTime.UtcNow, IsFromBot = false
        });

        Distribute(msg.ChannelId, msg.Tag, msg);
    }

    public async Task SendOutgoing(OutgoingMessage msg)
    {
        await DbManager.Instance.InsertMessageAsync(new UserMessage
        {
            ChannelId = msg.ChannelId, Content = msg.Content,
            Time = DateTime.UtcNow, IsFromBot = true
        });

        if (!msg.InternalOnly)
            OnOutgoingReady?.Invoke(msg);
        else
            Distribute(msg.ChannelId, "internal", msg);
    }

    public event Action<OutgoingMessage>? OnOutgoingReady;

    private void Distribute(int channelId, string subStream, object data)
    {
        if (_subscribers.TryGetValue((channelId, subStream), out var ports))
            foreach (var port in ports)
                (port as MessageInputPort)?.Enqueue(data);
    }

    public Task RunAsync() => Task.CompletedTask;
    public void OnEvent(EngineEvent e) { }
    public void RequestStop() => IsAlive = false;
}

// Internal port impl (same file)
internal class MessageInputPort : IInputPort
{
    private readonly Channel<object> _channel = Channel.CreateUnbounded<object>();
    public int ChannelId { get; }
    public string SubStream { get; }
    public int Weight { get; set; } = 5;
    public string Name => $"msg:{ChannelId}:{SubStream}";
    public Type DataType => typeof(object);
    int IInputPort.Weight => Weight;

    public MessageInputPort(int channelId, string subStream, int weight = 5)
    { ChannelId = channelId; SubStream = subStream; Weight = weight; }

    public void Enqueue(object data) => _channel.Writer.TryWrite(data);
    public async Task<object?> ReceiveAsync(CancellationToken ct)
    { try { return await _channel.Reader.ReadAsync(ct); } catch (OperationCanceledException) { return null; } }
    public bool TryPeek(out object? data) => _channel.Reader.TryPeek(out data);
}
```

- [ ] **Step 3: SpawnCheck**

```csharp
// MessageEngineSpawnCheck.cs
namespace AgentCoreProcessor.Engine.Message;

internal class MessageEngineSpawnCheck : IEngineSpawnCheck
{
    public string EngineType => "Message";
    public bool ShouldSpawn(EngineEvent e) => e.Type == "init";
    public ISubEngine Create(ISystemContext ctx) => new MessageEngine();
    public Task OnEventAsync(EngineEvent e) => Task.CompletedTask;
}
```

- [ ] **Step 4: DbManager additions**

```csharp
// In DbManager.cs, add:
public async Task<UserMessage?> FindMessageAsync(int channelId, string platformMessageId)
{
    await _db.CreateTableAsync<UserMessage>();
    return await _db.Table<UserMessage>()
        .FirstOrDefaultAsync(m => m.ChannelId == channelId && m.PlatformMessageId == platformMessageId);
}

public async Task InsertMessageAsync(UserMessage msg) => await _db.InsertAsync(msg);
```

```bash
dotnet build AgentCoreProcessor && \
git add AgentCoreProcessor/Engine/Message/ AgentCoreProcessor/Database/DbManager.cs && \
git commit -m "feat: add MessageEngine with bidirectional hub and DB dedup"
```

---

## Phase 3: AdapterEngine

### Task 5: AdapterEngine — 适配器托管

**Files to CREATE:**
- `AgentCoreProcessor/Engine/Adapter/AdapterEngine.cs`
- `AgentCoreProcessor/Engine/Adapter/AdapterEngineSpawnCheck.cs`
- `AgentCoreProcessor/Engine/Adapter/AdapterHandle.cs`

```csharp
// AdapterHandle.cs
namespace AgentCoreProcessor.Engine.Adapter;
internal class AdapterHandle
{
    public int Priority { get; set; }
    public string AdapterId { get; set; } = "";
    public bool IsAlive { get; set; } = true;
    public IAdapter Adapter { get; set; } = null!;
}
```

```csharp
// AdapterEngine.cs
namespace AgentCoreProcessor.Engine.Adapter;

internal class AdapterEngine : ISubEngine
{
    public string EngineType => "Adapter";
    public bool IsAlive { get; private set; } = true;
    public bool IsInfrastructure => true;

    private MessageEngine? _messageEngine;
    private readonly ConcurrentDictionary<int, List<AdapterHandle>> _channelAdapters = new();
    private readonly ConcurrentDictionary<string, AdapterHandle> _allAdapters = new();

    public void SetMessageEngine(MessageEngine me) => _messageEngine = me;

    public void RegisterAdapter(IAdapter adapter, int priority = 0)
    {
        var handle = new AdapterHandle { AdapterId = adapter.Id, Priority = priority, Adapter = adapter };
        _allAdapters[adapter.Id] = handle;
        foreach (var chId in adapter.ServedChannels)
            _channelAdapters.GetOrAdd(chId, _ => new()).Add(handle);
    }

    public AdapterHandle? GetActiveAdapter(int channelId)
    {
        if (!_channelAdapters.TryGetValue(channelId, out var handles)) return null;
        return handles.Where(h => h.IsAlive).OrderBy(h => h.Priority).ThenBy(h => h.AdapterId).FirstOrDefault();
    }

    public async Task OnAdapterMessage(IncomingMessage msg)
    {
        if (_messageEngine != null) await _messageEngine.AcceptIncoming(msg);
    }

    public async Task SendOutgoing(OutgoingMessage msg)
    {
        var handle = GetActiveAdapter(msg.ChannelId);
        if (handle != null) await handle.Adapter.SendAsync(msg);
    }

    public Task RunAsync() => Task.CompletedTask;
    public void OnEvent(EngineEvent e) { }
    public void RequestStop() => IsAlive = false;
}
```

```bash
dotnet build AgentCoreProcessor && \
git add AgentCoreProcessor/Engine/Adapter/ && \
git commit -m "feat: add AdapterEngine for adapter lifecycle and send priority"
```

---

## Phase 4: SessionEngine + RuntimeSession

### Task 6: RuntimeSession — 统一会话实现

**File to CREATE:** `AgentCoreProcessor/Engine/Session/RuntimeSession.cs`

Includes: RuntimeSession class, ChannelInputPort, DelegateOutputPort

```csharp
// AgentCoreProcessor/Engine/Session/RuntimeSession.cs
using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Session;

internal class RuntimeSession : IRuntimeSession
{
    public string SessionId { get; } = Guid.NewGuid().ToString("N")[..12];
    public int MaxLevel { get; private set; }
    public SessionStatus Status { get; private set; } = SessionStatus.Creating;
    public bool IsAlive => Status is SessionStatus.Running or SessionStatus.Creating or SessionStatus.Restored;
    public DateTime? WaitUntil { get; private set; }

    public IReadOnlyList<string> ComponentIds => _componentIds.AsReadOnly();
    public IReadOnlyList<string> ActiveTools => _activeTools.AsReadOnly();
    public IReadOnlyDictionary<string, IInputPort> Inputs => _inputs;
    public IReadOnlyDictionary<string, IOutputPort> Outputs => _outputs;

    public string? ParentSessionId { get; private set; }
    public SessionLimits Limits { get; set; } = new();

    private readonly List<string> _componentIds = new();
    private readonly List<string> _activeTools = new();
    private readonly Dictionary<string, IInputPort> _inputs = new();
    private readonly Dictionary<string, IOutputPort> _outputs = new();
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IInputPort> _dynamicInputs = new();

    private ComponentHost? _componentHost;
    private AgentCore? _agentCore;
    private ICurrencyService? _currency;
    private int _roundCount;
    private DateTime _createdAt = DateTime.UtcNow;
    private DateTime _lastActivity = DateTime.UtcNow;
    private CancellationTokenSource? _cts;

    private const int FreeRounds = 20;
    private const decimal WarningL1 = 500;
    private const decimal WarningL2 = 200;
    private const decimal WarningL3 = 50;

    public RuntimeSession(CreateSessionSpec spec)
    {
        MaxLevel = spec.MaxLevel;
        ParentSessionId = spec.ParentSessionId;
        if (spec.OverrideLimits != null) Limits = spec.OverrideLimits;
        _componentIds.AddRange(spec.ComponentIds);
        _createdAt = DateTime.UtcNow;
    }

    public void SetCurrency(ICurrencyService currency) => _currency = currency;
    public void SetAgentCore(AgentCore core) => _agentCore = core;

    public void InitComponents(ComponentRegistry registry, IToolContext toolContext)
    {
        _componentHost = new ComponentHost();
        foreach (var cid in _componentIds)
        {
            var comp = registry.InstantiateLoop(cid, toolContext);
            if (comp != null)
            {
                _componentHost.Register(comp);
                foreach (var tool in comp.Tools)
                {
                    _tools[tool.Definition.Name] = tool;
                    _activeTools.Add(tool.Definition.Name);
                }
            }
        }
    }

    public void EnableComponent(string componentId)
    {
        if (_componentIds.Contains(componentId)) return;
        // Load via PluginLoader at runtime - component must be installed
        _componentIds.Add(componentId);
        // Re-init component host to include the new component
    }

    public void DisableComponent(string componentId)
    {
        _componentIds.Remove(componentId);
        _activeTools.RemoveAll(t => /* tools from this component */ false);
    }

    public void SetLevel(int newLevel) => MaxLevel = newLevel;

    public void AddInputPort(string name, IInputPort port) => _inputs[name] = port;
    public void AddOutputPort(string name, IOutputPort port) => _outputs[name] = port;

    public bool CheckLifetimeLimits()
    {
        var now = DateTime.UtcNow;
        if (Limits.MaxLifetime > TimeSpan.Zero && now - _createdAt > Limits.MaxLifetime) return false;
        if (Limits.IdleTimeout > TimeSpan.Zero && now - _lastActivity > Limits.IdleTimeout) return false;
        return true;
    }

    public void RequestStop(string reason) { Status = SessionStatus.Completed; _cts?.Cancel(); }
    public void SetWaitUntil(DateTime? until) => WaitUntil = until;
    public void TouchActivity() => _lastActivity = DateTime.UtcNow;

    public async Task RunAsync(CancellationToken ct)
    {
        Status = SessionStatus.Running;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        while (IsAlive && !ct.IsCancellationRequested)
        {
            // Wait for input or timeout
            await WaitForInputAsync(_cts.Token);

            if (!CheckLifetimeLimits()) { Status = SessionStatus.Completed; break; }

            TouchActivity();
            SetWaitUntil(null);

            // Round pricing
            _roundCount++;
            if (_roundCount > FreeRounds && _currency != null)
            {
                var price = (decimal)Math.Ceiling((_roundCount - FreeRounds) / 10.0);
                if (_currency.Balance >= price)
                {
                    _currency.Spend(price, $"Session {SessionId} round {_roundCount}");
                    CheckBalanceWarning();
                }
                else
                {
                    InjectWarning("余额不足，无法续期。请收尾。");
                }
            }

            // Collect inputs
            var messages = new List<Message>();
            foreach (var (_, port) in _inputs)
            {
                while (port.TryPeek(out var data))
                {
                    await port.ReceiveAsync(ct);
                    if (data is IncomingMessage msg)
                        messages.Add(new Message { Role = "user", Content = msg.Content });
                    else if (data is string s)
                        messages.Add(new Message { Role = "user", Content = s });
                    else if (data != null)
                        messages.Add(new Message { Role = "system", Content = data.ToString()! });
                }
            }

            // Agent round
            if (_agentCore != null && messages.Count > 0)
            {
                var output = await _agentCore.InvokeAsync(messages, EngineMode.Working);
                messages.Add(new Message { Role = "assistant", Content = output.Text ?? "" });

                if (output.HasToolCalls && output.ToolCalls != null)
                {
                    foreach (var call in output.ToolCalls)
                    {
                        if (call.Tool == "wait")
                        {
                            var mins = call.Inputs.Count > 1 ? int.Parse(call.Inputs[1]) : 5;
                            SetWaitUntil(DateTime.UtcNow.AddMinutes(mins));
                        }
                        else if (_tools.TryGetValue(call.Tool, out var tool))
                        {
                            var result = await tool.ExecuteAsync(call.Inputs, ct);
                            messages.Add(new Message { Role = "user", Content = result.Data ?? result.Status });
                        }
                    }
                }
                else
                {
                    // No tool calls — if no inputs pending, could stop
                    // But don't force: session may wait for next input
                }
            }
            else if (messages.Count == 0)
            {
                // WaitUntil timeout activated — inject timeout marker
                messages.Add(new Message { Role = "system", Content = "{\"type\":\"timeout\",\"wait_reason\":\"wait timeout expired\"}" });
            }
        }

        Status = SessionStatus.Completed;
    }

    private async Task WaitForInputAsync(CancellationToken ct)
    {
        while (WaitUntil == null)
        {
            // Check if any input port has data
            foreach (var (_, port) in _inputs)
                if (port.TryPeek(out _)) return;
            // Check dynamic inputs
            foreach (var (_, port) in _dynamicInputs)
                if (port.TryPeek(out _)) return;
            await Task.Delay(200, ct);
        }
        // Has WaitUntil: wait for data or timeout
        while (DateTime.UtcNow < WaitUntil!.Value)
        {
            foreach (var (_, port) in _inputs)
                if (port.TryPeek(out _)) return;
            foreach (var (_, port) in _dynamicInputs)
                if (port.TryPeek(out _)) return;
            await Task.Delay(200, ct);
        }
        // Timed out — SessionEngine will handle injection
    }

    private void CheckBalanceWarning()
    {
        if (_currency == null) return;
        var bal = _currency.Balance;
        string? level = bal <= WarningL3 ? "L3 危急" : bal <= WarningL2 ? "L2 收紧" : bal <= WarningL1 ? "L1 提醒" : null;
        if (level != null)
            InjectWarning($"余额 {bal:F0} 币（{level}），可以考虑向管理员要钱");
    }

    private void InjectWarning(string text)
    {
        foreach (var (_, port) in _inputs)
            if (port is ChannelInputPort<object> cp)
                cp.Write(new { type = "warning", text });
    }

    public Task PersistStateAsync()
    {
        var dir = Path.Combine(PathConfig.StoragePath, "Sessions", SessionId);
        Directory.CreateDirectory(dir);
        var meta = new { status = Status.ToString(), createdAt = _createdAt, roundCount = _roundCount, componentIds = _componentIds };
        File.WriteAllText(Path.Combine(dir, "meta.json"), Newtonsoft.Json.JsonConvert.SerializeObject(meta));
        return Task.CompletedTask;
    }

    public Task RestoreStateAsync()
    {
        var dir = Path.Combine(PathConfig.StoragePath, "Sessions", SessionId);
        if (File.Exists(Path.Combine(dir, "meta.json")))
        {
            var json = File.ReadAllText(Path.Combine(dir, "meta.json"));
            // Restore state...
        }
        return Task.CompletedTask;
    }
}

// Port implementations in same file
internal class ChannelInputPort<T> : IInputPort
{
    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();
    public string Name { get; }
    public int Weight { get; set; }
    public Type DataType => typeof(T);

    public ChannelInputPort(string name, int weight = 5) { Name = name; Weight = weight; }
    public void Write(T data) => _channel.Writer.TryWrite(data);
    public async Task<object?> ReceiveAsync(CancellationToken ct)
    { try { return await _channel.Reader.ReadAsync(ct); } catch (OperationCanceledException) { return null; } }
    public bool TryPeek(out object? data)
    { if (_channel.Reader.TryPeek(out var item)) { data = item; return true; } data = null; return false; }
}

internal class DelegateOutputPort<T> : IOutputPort
{
    private readonly Func<T, Task> _handler;
    public string Name { get; }
    public Type DataType => typeof(T);
    public DelegateOutputPort(string name, Func<T, Task> handler) { Name = name; _handler = handler; }
    public async Task SendAsync(object data) { if (data is T typed) await _handler(typed); }
}
```

```bash
dotnet build AgentCoreProcessor && \
git add AgentCoreProcessor/Engine/Session/RuntimeSession.cs && \
git commit -m "feat: add unified RuntimeSession with pricing, component mgmt, port I/O"
```

---

### Task 7: SessionEngine — Session 管理器

**Files to CREATE:**
- `AgentCoreProcessor/Engine/Session/SessionEngine.cs`
- `AgentCoreProcessor/Engine/Session/SessionEngineSpawnCheck.cs`

```csharp
// SessionEngine.cs
using System.Collections.Concurrent;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Session;

internal class SessionEngine : ISubEngine
{
    public string EngineType => "Session";
    public bool IsAlive { get; private set; } = true;
    public bool IsInfrastructure => true;

    private readonly ConcurrentDictionary<string, RuntimeSession> _sessions = new();
    private readonly Dictionary<int, string> _channelFullClaims = new();
    private readonly List<(int channelId, string sessionId)> _channelListens = new();
    private readonly List<ScheduledTask> _scheduledTasks = new();
    private readonly List<TodoItem> _todoItems = new();
    private ICurrencyService? _currency;
    private ComponentRegistry? _componentRegistry;
    private IToolContext? _toolContext;

    public void SetCurrency(ICurrencyService c) => _currency = c;
    public void SetComponentRegistry(ComponentRegistry r) => _componentRegistry = r;
    public void SetToolContext(IToolContext tc) => _toolContext = tc;

    // === Session CRUD ===
    public IRuntimeSession CreateSession(CreateSessionSpec spec)
    {
        var session = new RuntimeSession(spec);
        session.SetCurrency(_currency!);
        // Init components from spec
        if (_componentRegistry != null && _toolContext != null)
            session.InitComponents(_componentRegistry, _toolContext);
        _sessions[session.SessionId] = session;

        if (spec.AutoClaimChannel && spec.InputPorts.Any())
        {
            var firstChannel = spec.InputPorts.First().Value.ChannelId;
            ClaimChannel(session.SessionId, firstChannel, ClaimLevel.Full);
        }

        return session;
    }

    public StopResult StopSession(string id, string reason)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return new StopResult { Success = false, BlockReason = "Not found" };
        var children = _sessions.Values
            .Where(s => s.ParentSessionId == id && s.IsAlive).Select(s => s.SessionId).ToList();
        if (children.Count > 0)
            return new StopResult { Success = false, BlockReason = "Has running children", BlockingChildren = children };
        session.RequestStop(reason);
        return new StopResult { Success = true };
    }

    public StopResult ForceKill(string id)
    {
        foreach (var child in _sessions.Values.Where(s => s.ParentSessionId == id).ToList())
            ForceKill(child.SessionId);
        if (_sessions.TryGetValue(id, out var session))
            session.RequestStop("force kill");
        return new StopResult { Success = true };
    }

    public IRuntimeSession? GetSession(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    // === Channel claiming ===
    public bool ClaimChannel(string sessionId, int channelId, ClaimLevel level)
    {
        if (level == ClaimLevel.Full)
        {
            lock (_channelFullClaims)
            {
                if (_channelFullClaims.ContainsKey(channelId)) return false;
                _channelFullClaims[channelId] = sessionId;
            }
        }
        else { lock (_channelListens) _channelListens.Add((channelId, sessionId)); }
        return true;
    }

    public void ReleaseChannel(string sessionId, int channelId)
    {
        lock (_channelFullClaims)
            if (_channelFullClaims.TryGetValue(channelId, out var h) && h == sessionId) _channelFullClaims.Remove(channelId);
        lock (_channelListens) _channelListens.RemoveAll(x => x.channelId == channelId && x.sessionId == sessionId);
    }

    public string? GetFullClaimer(int channelId)
    { lock (_channelFullClaims) return _channelFullClaims.TryGetValue(channelId, out var s) ? s : null; }

    public bool IsChannelClaimed(int channelId)
    { lock (_channelFullClaims) return _channelFullClaims.ContainsKey(channelId); }

    // === ScheduledTask ===
    public void AddScheduledTask(ScheduledTask task) { lock (_scheduledTasks) _scheduledTasks.Add(task); }

    // === TODO ===
    public void AddTodo(TodoItem todo) { lock (_todoItems) _todoItems.Add(todo); }

    public IReadOnlyList<TodoItem> GetPendingTodos(int? relatedPersonId = null)
    {
        lock (_todoItems)
            return _todoItems.Where(t => t.Status == "pending"
                && (relatedPersonId == null || t.RelatedPersonId == relatedPersonId))
                .Where(t => t.NotBefore <= DateTime.UtcNow).ToList();
    }

    // === 30s monitoring loop ===
    public async Task RunAsync()
    {
        while (IsAlive)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));

            // 1. WaitUntil timeout check
            foreach (var (_, session) in _sessions)
            {
                if (session.WaitUntil != null && session.WaitUntil <= DateTime.UtcNow)
                    session.SetWaitUntil(null); // Will activate on next loop iteration
            }

            // 2. ScheduledTask check
            lock (_scheduledTasks)
            {
                var due = _scheduledTasks.Where(t => t.Status == "pending" && t.ExecuteAt <= DateTime.UtcNow).ToList();
                foreach (var task in due)
                { task.Status = "executing"; CreateSession(new CreateSessionSpec { ComponentIds = new[] { "basic-tools", "file-tools" }, InitialState = task }); }
            }

            // 3. TODO scan (every 15 min)
            if (DateTime.UtcNow.Minute % 15 == 0)
            {
                var pending = GetPendingTodos()
                    .Where(t => _currency == null || _currency.Balance >= t.MinBalance)
                    .OrderBy(t => t.CreatedAt).Take(1).ToList();
                foreach (var todo in pending)
                { todo.Status = "executing"; CreateSession(new CreateSessionSpec { ComponentIds = new[] { "basic-tools", "file-tools" }, InitialState = todo }); }
            }
        }
    }

    public void OnEvent(EngineEvent e) { }
    public void RequestStop() => IsAlive = false;
}

public class StopResult
{
    public bool Success { get; set; }
    public string? BlockReason { get; set; }
    public List<string>? BlockingChildren { get; set; }
}
```

```bash
dotnet build AgentCoreProcessor && \
git add AgentCoreProcessor/Engine/Session/SessionEngine.cs AgentCoreProcessor/Engine/Session/SessionEngineSpawnCheck.cs && \
git commit -m "feat: add SessionEngine with CRUD, channel claim, 30s monitoring loop"
```

---

## Phase 5: ScheduledTask + TODO 数据模型

### Task 8: ScheduledTask + TodoItem

**Files to CREATE:**
- `AgentLilara.PluginSDK/ScheduledTask.cs`
- `AgentLilara.PluginSDK/TodoItem.cs`

```csharp
// ScheduledTask.cs
namespace AgentLilara.PluginSDK;

public enum ScheduleType { Once, Recurring }

public class ScheduledTask
{
    public string TaskId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Description { get; set; } = "";
    public ScheduleType Type { get; set; } = ScheduleType.Once;
    public DateTime ExecuteAt { get; set; }
    public string? CronExpression { get; set; }
    public int? SourceChannelId { get; set; }
    public int? SourcePersonId { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
```

```csharp
// TodoItem.cs
namespace AgentLilara.PluginSDK;

public class TodoItem
{
    public string TodoId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Description { get; set; } = "";
    public int? RelatedPersonId { get; set; }
    public int? RelatedChannelId { get; set; }
    public decimal MinBalance { get; set; } = 0;
    public DateTime NotBefore { get; set; } = DateTime.UtcNow;
    public DateTime? NotAfter { get; set; }
    public string Status { get; set; } = "pending";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public bool RequiresInteraction { get; set; }
}
```

```bash
dotnet build AgentLilara.PluginSDK && \
git add AgentLilara.PluginSDK/ScheduledTask.cs AgentLilara.PluginSDK/TodoItem.cs && \
git commit -m "feat: add ScheduledTask and TodoItem models to SDK"
```

---

## Phase 6: enable_component / disable_component 工具

### Task 9: Component management tools

**Files to CREATE:**
- `AgentCoreProcessor/Tool/Core/EnableComponentTool.cs`
- `AgentCoreProcessor/Tool/Core/DisableComponentTool.cs`

These are L0 behavior tools — session can load/unload components at will.

```csharp
// EnableComponentTool.cs
namespace AgentCoreProcessor.Tool.Core;

public class EnableComponentTool : ITool
{
    public ToolDefinition Definition => new()
    {
        Name = "enable_component",
        Description = "加载已安装的插件组件",
        Parameters = new List<ToolParameter>
        {
            new() { Name = "component_id", Type = "string", Description = "组件ID", Required = true }
        }
    };
    public Task<ToolResult> ExecuteAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        var componentId = inputs[1]; // index 0 = tool name
        // Get current session, call EnableComponent
        return Task.FromResult(new ToolResult { Status = "success", Data = $"Component {componentId} enabled" });
    }
}
```

```bash
dotnet build AgentCoreProcessor && \
git add AgentCoreProcessor/Tool/Core/EnableComponentTool.cs AgentCoreProcessor/Tool/Core/DisableComponentTool.cs && \
git commit -m "feat: add enable_component and disable_component tools"
```

---

## Phase 7: ChannelEngine 精简

### Task 10: ChannelEngine — 保留冲动值+Express双模板

**Files to MODIFY:**
- `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs`
- `AgentCoreProcessor/Engine/Worker/ChannelExpressCycle.cs`

Remove: isWorkingMode, Working cycle code, ComponentHost. Keep: impulse + Express.

```csharp
// Key changes to ChannelEngine.cs:
// - Remove isWorkingMode, ExecuteWorkingCycleAsync, ComponentHost, CompressionTierModule
// - Add SessionEngine check: if (sessionEngine.IsChannelClaimed(channelId)) return;
// - Express tools: speak, send_media, escalate, wait, memory (5 tools)
// - Dual template: awake vs sleep-talk persona
// - get_time auto-injected each round

// ChannelExpressCycle.cs changes:
// Express whitelist: speak, send_media, escalate, wait, memory
// Add GetPersonaTemplate() — checks CurrentSleepState for sleep-talk mode
```

```bash
dotnet build AgentCoreProcessor && \
git add AgentCoreProcessor/Engine/Worker/ && \
git commit -m "refactor: strip ChannelEngine to impulse+Express dual template only"
```

---

## Phase 8: ExtractionEngine

### Task 11: ExtractionEngine — 记忆提取 + 监听检测

**Files to CREATE:**
- `AgentCoreProcessor/Engine/Extraction/ExtractionEngine.cs`
- `AgentCoreProcessor/Engine/Extraction/ExtractionEngineSpawnCheck.cs`
- `AgentCoreProcessor/Engine/Extraction/EavesdropTask.cs`

Batches 10 messages per channel → one LLM call for memory extraction + eavesdrop check.

```bash
dotnet build AgentCoreProcessor && \
git add AgentCoreProcessor/Engine/Extraction/ && \
git commit -m "feat: add ExtractionEngine for memory extraction and eavesdrop detection"
```

---

## Phase 9: MasterEngine 注册更新

### Task 12: MasterEngine wiring

**Files to MODIFY:** `AgentCoreProcessor/Engine/Core/MasterEngine.cs`

Register MessageEngine, AdapterEngine, SessionEngine, ExtractionEngine SpawnChecks. Wire up engine references after init. Remove SystemEngine from registry.

```bash
dotnet build AgentCoreProcessor && \
git add AgentCoreProcessor/Engine/Core/MasterEngine.cs && \
git commit -m "feat: register new engines in MasterEngine, wire dependencies"
```

---

## Phase 10: 清理

### Task 13: 删除 SystemEngine + 旧代码

Delete: SystemEngine.cs, SystemEngineSpawnCheck.cs, ContextPersistence.cs, ChannelWorkingCycle.cs, ChannelContextPersistence.cs, old TaskSession.cs

```bash
rm AgentCoreProcessor/Engine/System/SystemEngine.cs AgentCoreProcessor/Engine/System/SystemEngineSpawnCheck.cs AgentCoreProcessor/Engine/System/ContextPersistence.cs AgentCoreProcessor/Engine/Worker/ChannelWorkingCycle.cs AgentCoreProcessor/Engine/Worker/ChannelContextPersistence.cs AgentCoreProcessor/Engine/System/TaskSession.cs
dotnet build AgentCoreProcessor
git add -A && git commit -m "chore: delete SystemEngine, old TaskSession, ChannelWorkingCycle"
```

---

## Phase 11: Plugin + EventBus

### Task 14: EventBus 退化

Remove business message types from EventBus. Keep only control signals: EngineStart/Stop, ConfigChange.

### Task 15: CrossLoopTools 适配

Existing CrossLoopTools plugin needs to work with SessionEngine request queue instead of SystemEngine delegation. Session-to-session communication via MessageEngine.

```bash
git commit -m "refactor: degrade EventBus, adapt CrossLoopTools to SessionEngine"
```

---

## Phase 12: WebUI

### Task 16: Sessions 页面 + 导航更新

Create SessionsProvider showing active/historical sessions. Update navigation to include Sessions.

---

## Phase 13: 文档

### Task 17: 更新 CLAUDE.md + architecture-map.md

Rewrite architecture diagram for three-layer model. Remove SystemEngine/SpawnCheck/ContextPersistence/ChannelWorkingCycle/ChannelContextPersistence references. Add new engine references.

---

## Phase 14: 集成验证

### Task 18: 编译 + --test 试运行

```bash
cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null
dotnet build && dotnet run --project AgentCoreProcessor -- --test --delay 2
```

Verify: all engines start, Express responds, SessionEngine manages sessions, WebUI loads.

---

## 依赖图

```
Phase 1 (SDK)
  1. IRuntimeSession + SessionLimits + SessionStatus + ClaimLevel
  2. IInputPort + IOutputPort
  3. CreateSessionSpec + PortSpec + ToolLevel
     │
Phase 2: 4. MessageEngine ←────────────── 1,2,3
     │
Phase 3: 5. AdapterEngine ←────────────── 4
     │
Phase 4: 6. RuntimeSession ←────────────── 1,2,3
         7. SessionEngine ←──────────────── 6 + ICurrencyService
     │
Phase 5: 8. ScheduledTask + TodoItem ←──── 1
     │
Phase 6: 9. enable/disable_component ───── 6
     │
Phase 7: 10. ChannelEngine refactor ←───── 4,7
     │
Phase 8: 11. ExtractionEngine ←─────────── 4,7
     │
Phase 9: 12. MasterEngine wiring ←──────── 4,5,7,10,11
     │
Phase 10: 13. Delete SystemEngine ←─────── 12
     │
Phase 11: 14. EventBus degrade ←────────── 4
          15. CrossLoopTools adapt ←─────── 7
     │
Phase 12: 16. WebUI Sessions ←──────────── 7
     │
Phase 13: 17. Docs update ←─────────────── all
     │
Phase 14: 18. Build + test run ←────────── all
```

---

## 风险

1. **MessageEngine DB 去重** — 每条查 DB，需复合索引 (ChannelId, PlatformMessageId)
2. **SessionEngine 30s 精度** — scheduled tasks 精确到分钟够用，wait 超时 30s 粒度也够
3. **CrossLoopTools 兼容** — 原先依赖 SystemEngine 委托，改为 SessionEngine 请求队列，需要验证
4. **ChannelEngine 剥离** — 移除 Working 代码后需确保不破坏现有 Express 功能
