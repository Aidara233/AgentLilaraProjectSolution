# Engine Unification Phase 1: Core Loop Unification

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unify the core agent loop: extract Gate (delegate-driven loop skeleton, EventBus+ForceWake), Agent (reusable multi-round reasoning loop), IAgentHost, three-tier compression, and channel persistence. Apply to both ChannelEngine and SystemEngine.

**Architecture:** Gate wraps LoopGate + EventBus, uses delegates (not abstract) so engines compose it without multi-inheritance conflicts. Agent is a concrete class wrapping AgentCore's multi-round loop. Both engines implement IAgentHost to feed context. SystemEngine already has stacked context; ChannelEngine gets the same pattern. Three-tier compression replaces SystemEngine's single-threshold. ChannelEngine gets persistence mirroring SystemEngine's.

**Tech Stack:** .NET 8, C#, Newtonsoft.Json

**Out of scope (Phase 2):** IInjectProvider replacement, plugin constructor injection, EngineModule → IInjectProvider migration.

---

## File Structure

**New:**
| File | Responsibility |
|------|---------------|
| `AgentCoreProcessor/Engine/Core/Gate.cs` | Gate: delegate-driven loop skeleton, EventBus subscription, Signal/ForceWake, WaitForTriggerAsync |
| `AgentCoreProcessor/Engine/Core/IAgentHost.cs` | IAgentHost: BuildStartInjectAsync, BuildRoundInjectAsync |
| `AgentCoreProcessor/Engine/Core/Agent.cs` | Agent: multi-round loop, model call → tool execute → stop decision, backoff |
| `AgentCoreProcessor/Engine/Core/AgentConfig.cs` | AgentConfig: MaxRounds, backoff array, compress thresholds |
| `AgentCoreProcessor/Engine/Modules/CompressionTierModule.cs` | Three-tier compression: L1/L2/L3 evaluation + async/sync compress |
| `AgentCoreProcessor/Tool/Core/CompressTool.cs` | compress tool: model-callable, all modes |
| `AgentCoreProcessor/Engine/Worker/ChannelContextPersistence.cs` | Channel context persistence: per-channel JSON atomic write |

**Modified:**
| File | Change |
|------|--------|
| `AgentCoreProcessor/Engine/System/SystemEngine.cs` | Gate+Agent+IAgentHost, upgrade compression to 3-tier, remove old inline loop |
| `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs` | Stacked context, Gate+Agent+IAgentHost, persistence, Express still direct-Core |
| `AgentCoreProcessor/Engine/System/ContextPersistence.cs` | Extract interface, minor API tweak |
| `AgentCoreProcessor/Engine/System/ContextCompressionModule.cs` | Integrate with CompressionTierModule |

**Not changed:**
| File | Why |
|------|-----|
| `AgentCoreProcessor/Engine/Worker/LoopGate.cs` | Stays as TCS primitive, no changes |
| `AgentCoreProcessor/Engine/Worker/EngineModule.cs` | Not touched (Phase 2) |
| `AgentCoreProcessor/Engine/Worker/ILoopBus.cs` | Not touched (Phase 2) |
| `AgentCoreProcessor/Core/AgentCore.cs` | Stays — Agent wraps it |
| `AgentCoreProcessor/Core/PromptBuilder.cs` | Not needed after refactor (Agent builds messages directly) |

---

### Task 1: Gate class

**Files:**
- Create: `AgentCoreProcessor/Engine/Core/Gate.cs`

Gate wraps LoopGate + EventBus with delegate hooks (not abstract methods), avoiding multi-inheritance conflicts with ISubEngine.

- [ ] **Step 1: Write Gate.cs**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 循环闸门。封装"等触发→判条件→跑执行"的循环骨架。
    /// 使用 delegate 而非抽象方法，引擎组合 Gate 而不继承。
    /// </summary>
    internal class Gate
    {
        private readonly LoopGate _inner = new();
        private readonly EventBus _eventBus;
        private volatile bool _forceWake;

        public Gate(EventBus eventBus)
        {
            _eventBus = eventBus;
        }

        /// <summary>内部唤醒（引擎/定时器回调调用）。</summary>
        public void Signal() => _inner.Signal();

        /// <summary>强制唤醒，跳过 ShouldActivate 直接开闸。</summary>
        public void ForceWake()
        {
            _forceWake = true;
            _inner.Signal();
        }

        /// <summary>引擎注入：评估是否开闸。返回 true 开闸。</summary>
        public Func<Task<bool>>? ShouldActivate { get; set; }

        /// <summary>引擎注入：执行本轮工作。</summary>
        public Func<CancellationToken, Task>? ExecuteAsync { get; set; }

        /// <summary>
        /// 等待任一触发源：EventBus 事件 / Signal() / 超时 / CTS 取消。
        /// </summary>
        public async Task<bool> WaitForTriggerAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            var busTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void handler(EngineEvent e)
            {
                busTcs.TrySetResult(true);
            }

            _eventBus.OnEvent += handler;
            try
            {
                var innerTask = _inner.WaitAsync(timeout, ct);
                var completed = await Task.WhenAny(innerTask, busTcs.Task);

                if (completed == busTcs.Task)
                    return true;

                return await innerTask;
            }
            finally
            {
                _eventBus.OnEvent -= handler;
            }
        }

        /// <summary>循环骨架。引擎调用此方法启动循环，不再写 while。</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await WaitForTriggerAsync(Timeout.InfiniteTimeSpan, ct);

                bool isForceWake = _forceWake;
                _forceWake = false;

                if (!isForceWake && ShouldActivate != null && !await ShouldActivate())
                    continue;

                if (ExecuteAsync != null)
                    await ExecuteAsync(ct);
            }
        }
    }
}
```

- [ ] **Step 2: Build**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && dotnet build
```
Expected: Build succeeds (Gate not yet wired in).

- [ ] **Step 3: Commit**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && git add AgentCoreProcessor/Engine/Core/Gate.cs && git commit -m "feat: add Gate — delegate-driven loop skeleton with EventBus and ForceWake"
```

---

### Task 2: IAgentHost + AgentConfig

**Files:**
- Create: `AgentCoreProcessor/Engine/Core/IAgentHost.cs`
- Create: `AgentCoreProcessor/Engine/Core/AgentConfig.cs`

- [ ] **Step 1: Write IAgentHost.cs**

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// Agent 宿主接口。Engine 实现，Agent 通过此接口拉取上下文注入。
    /// </summary>
    internal interface IAgentHost
    {
        /// <summary>每次 Agent.RunAsync() 启动时调一次。新消息、压缩产物等一次性内容。</summary>
        Task<List<Message>?> BuildStartInjectAsync();

        /// <summary>Agent 每轮调一次。轮次提示、压缩提醒、模式说明等持续内容。</summary>
        Task<List<Message>?> BuildRoundInjectAsync();
    }
}
```

- [ ] **Step 2: Write AgentConfig.cs**

```csharp
namespace AgentCoreProcessor.Engine
{
    internal class AgentConfig
    {
        public int MaxRounds { get; set; } = 20;
        public int[] BackoffSeconds { get; set; } = { 10, 30, 60, 120, 300 };
        public int CompressL1Tokens { get; set; } = 30000;
        public int CompressL2Tokens { get; set; } = 50000;
        public int CompressL3Tokens { get; set; } = 70000;
        public int CompressMinTokens { get; set; } = 5000;
        public int CompressRetainedMessageCount { get; set; } = 6;
        public int CompressRetainedMaxTokens { get; set; } = 2000;
    }
}
```

- [ ] **Step 3: Build and commit**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && dotnet build
```
```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && git add AgentCoreProcessor/Engine/Core/IAgentHost.cs AgentCoreProcessor/Engine/Core/AgentConfig.cs && git commit -m "feat: add IAgentHost interface and AgentConfig"
```

---

### Task 3: Agent class

**Files:**
- Create: `AgentCoreProcessor/Engine/Core/Agent.cs`

Extracts multi-round Working-mode loop from `SystemEngine.RunAgentLoopAsync` (lines 306-399) and `ChannelEngine.ProcessResponseAsync+DecideNext` (lines 863-1011).

- [ ] **Step 1: Write Agent.cs**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 可复用 Agent。封装"构建上下文→调模型→执行工具→是否继续"的多轮推理循环。
    /// 仅处理 Working 模式。Express 模式由 Engine 直接调 Core，不走 Agent。
    /// </summary>
    internal class Agent
    {
        private readonly IAgentHost _host;
        private readonly AgentCore _core;
        private readonly AgentConfig _config;
        private readonly HashSet<string> _authorizedTools;
        private readonly List<Message> _history = new();
        private int _consecutiveFailures;
        private DateTime? _backoffUntil;

        public AgentStopReason? StopReason { get; private set; }
        public List<ServiceEventArgs> History => _history;
        public int TotalRounds { get; private set; }
        public bool IsInBackoff => _backoffUntil.HasValue && DateTime.Now < _backoffUntil.Value;
        public List<ToolCall>? LastRoundCalls { get; private set; }
        public List<ToolResult>? LastRoundResults { get; private set; }

        public Agent(IAgentHost host, AgentCore core, AgentConfig config, HashSet<string> authorizedTools)
        {
            _host = host;
            _core = core;
            _config = config;
            _authorizedTools = authorizedTools ?? new HashSet<string>();
        }

        public async Task RunAsync(CancellationToken ct)
        {
            StopReason = null;
            _consecutiveFailures = 0;
            TotalRounds = 0;
            LastRoundCalls = null;
            LastRoundResults = null;

            // 启动注入（新消息、压缩产物等一次性内容，一条 user message）
            var startInject = await _host.BuildStartInjectAsync();
            if (startInject != null)
            {
                foreach (var m in startInject)
                    _history.Add(m);
            }

            for (int round = 0; round < _config.MaxRounds && !ct.IsCancellationRequested; round++)
            {
                if (IsInBackoff)
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);

                TotalRounds++;
                using var roundSpan = Signal.Open(LogGroup.Engine, "agent:round",
                    new { round = round + 1 });

                // 本轮注入
                var roundInject = await _host.BuildRoundInjectAsync();

                // 拼装消息：历史 + 本轮注入 + 上轮工具结果
                var messages = new List<Message>(_history);
                if (roundInject != null)
                    messages.AddRange(roundInject);
                if (LastRoundResults != null && LastRoundCalls != null && LastRoundResults.Count > 0)
                    messages.Add(FormatToolResults(LastRoundCalls, LastRoundResults));

                // 调模型
                ModelOutput output;
                using (var modelSpan = Signal.Open(LogGroup.Model, "core:invoke",
                    new { messageCount = messages.Count, round = round + 1 }))
                {
                    try
                    {
                        output = await _core.InvokeAsync(messages, EngineMode.Working);
                        _consecutiveFailures = 0;
                        _backoffUntil = null;
                        modelSpan.SetCloseDetail(new
                        {
                            isText = output.IsText,
                            hasToolCalls = output.HasToolCalls,
                            toolCount = output.ToolCalls?.Count ?? 0
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _consecutiveFailures++;
                        modelSpan.SetCloseDetail(new { error = ex.GetType().Name, message = ex.Message });

                        if (_consecutiveFailures > _config.BackoffSeconds.Length)
                        {
                            StopReason = AgentStopReason.Error;
                            return;
                        }
                        var delay = _config.BackoffSeconds[
                            Math.Min(_consecutiveFailures - 1, _config.BackoffSeconds.Length - 1)];
                        _backoffUntil = DateTime.Now.AddSeconds(delay);
                        Signal.Warn(LogGroup.Engine, "agent退避",
                            new { consecutiveFailures = _consecutiveFailures, backoffSeconds = delay });
                        continue;
                    }
                }

                // 追加 assistant 到历史
                _history.Add(FormatAssistant(output));

                // 无工具 → 停止
                if (!output.HasToolCalls || output.ToolCalls == null || output.ToolCalls.Count == 0)
                {
                    StopReason = AgentStopReason.Completed;
                    return;
                }

                // 执行工具
                List<ToolResult> results;
                using (var toolSpan = Signal.Open(LogGroup.Tool, "agent:tools",
                    new { toolCount = output.ToolCalls.Count, tools = string.Join(",", output.ToolCalls.Select(c => c.Tool)) }))
                {
                    var executor = new ToolExecutor(authorizedTools: _authorizedTools);
                    results = await executor.ExecuteAsync(output.ToolCalls);
                    toolSpan.SetCloseDetail(new
                    {
                        successCount = results.Count(r => r.Status == "ok"),
                        errorCount = results.Count(r => r.Error != null)
                    });
                }

                LastRoundCalls = output.ToolCalls;
                LastRoundResults = results;

                // wait 工具 → 停止
                if (output.ToolCalls.Any(c => c.Tool == "wait"))
                {
                    StopReason = AgentStopReason.WaitRequested;
                    return;
                }
            }

            StopReason = AgentStopReason.MaxRounds;
        }

        public void ForceStop() => StopReason = AgentStopReason.ForceStopped;

        public void ClearHistory()
        {
            _history.Clear();
            LastRoundCalls = null;
            LastRoundResults = null;
        }

        public void AddToHistory(Message msg) => _history.Add(msg);

        // ---- 格式化 ----

        private Message FormatAssistant(ModelOutput output)
        {
            if (output.HasToolCalls && output.ToolCalls != null)
            {
                if (_core.UseNativeTools)
                {
                    var parts = new List<ContentPart>();
                    if (!string.IsNullOrEmpty(output.Thinking))
                        parts.Add(ContentPart.FromText(output.Thinking));
                    foreach (var c in output.ToolCalls)
                    {
                        if (c.ToolUseId != null)
                        {
                            var json = c.Inputs.Count > 0
                                ? Newtonsoft.Json.JsonConvert.SerializeObject(c.Inputs)
                                : "{}";
                            parts.Add(ContentPart.FromToolUse(c.ToolUseId, c.Tool, json));
                        }
                    }
                    return new Message
                    {
                        Role = "assistant",
                        Content = output.Thinking ?? "[tool calls]",
                        ContentParts = parts
                    };
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    if (!string.IsNullOrEmpty(output.Thinking))
                        sb.AppendLine(output.Thinking);
                    foreach (var c in output.ToolCalls)
                        sb.AppendLine($"{c.Tool}({string.Join(", ", c.Inputs).Truncate(100)})");
                    return new Message { Role = "assistant", Content = sb.ToString() };
                }
            }
            return new Message { Role = "assistant", Content = output.Text ?? output.Thinking ?? "" };
        }

        private Message FormatToolResults(List<ToolCall> calls, List<ToolResult> results)
        {
            if (_core.UseNativeTools)
            {
                var parts = new List<ContentPart>();
                for (int i = 0; i < calls.Count && i < results.Count; i++)
                {
                    if (calls[i].ToolUseId != null)
                    {
                        var data = results[i].IsSuccess
                            ? (results[i].Data ?? "成功")
                            : $"失败: {results[i].Error ?? results[i].Status}";
                        parts.Add(ContentPart.FromToolResult(calls[i].ToolUseId!, data, !results[i].IsSuccess));
                    }
                }
                return new Message { Role = "user", Content = "[tool results]", ContentParts = parts };
            }
            else
            {
                var sb = new System.Text.StringBuilder("[上一轮工具执行结果]\n");
                for (int i = 0; i < calls.Count && i < results.Count; i++)
                {
                    if (results[i].IsSuccess)
                        sb.AppendLine($"[{calls[i].Tool}]: 成功");
                    else
                        sb.AppendLine($"[{calls[i].Tool}]: {results[i].Status} - {results[i].Error}");
                }
                return new Message { Role = "user", Content = sb.ToString() };
            }
        }
    }

    internal enum AgentStopReason
    {
        Completed,
        MaxRounds,
        WaitRequested,
        ForceStopped,
        Cancelled,
        Error
    }
}
```

- [ ] **Step 2: Build**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && dotnet build
```
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && git add AgentCoreProcessor/Engine/Core/Agent.cs && git commit -m "feat: add Agent — reusable multi-round loop with backoff"
```

---

### Task 4: Three-tier compression + compress tool

**Files:**
- Create: `AgentCoreProcessor/Engine/Modules/CompressionTierModule.cs`
- Create: `AgentCoreProcessor/Tool/Core/CompressTool.cs`

- [ ] **Step 1: Write CompressionTierModule.cs**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Engine.Modules
{
    internal class CompressionTierModule
    {
        private readonly AgentConfig _config;
        private readonly SummarizationCore _summarizer = new();
        private readonly Func<List<Message>> _getHistory;
        private readonly Action _onL3Triggered;

        private string? _currentSummary;
        private bool _l1Injected;
        private CompressionTier _currentTier = CompressionTier.None;
        private volatile bool _compressing;

        public CompressionTier CurrentTier => _currentTier;
        public string? CurrentSummary => _currentSummary;
        public bool IsCompressing => _compressing;

        public CompressionTierModule(AgentConfig config, Func<List<Message>> getHistory, Action onL3Triggered)
        {
            _config = config;
            _getHistory = getHistory;
            _onL3Triggered = onL3Triggered;
        }

        public void SetSummary(string? summary) => _currentSummary = summary;

        public CompressionTier Evaluate(int estimatedTokens)
        {
            if (estimatedTokens < _config.CompressMinTokens)
                _currentTier = CompressionTier.None;
            else if (estimatedTokens >= _config.CompressL3Tokens)
                _currentTier = CompressionTier.L3;
            else if (estimatedTokens >= _config.CompressL2Tokens)
                _currentTier = CompressionTier.L2;
            else if (estimatedTokens >= _config.CompressL1Tokens)
                _currentTier = CompressionTier.L1;
            else
                _currentTier = CompressionTier.None;
            return _currentTier;
        }

        public string? GetInjectText(int estimatedTokens)
        {
            _ = Evaluate(estimatedTokens);
            return _currentTier switch
            {
                CompressionTier.L1 when !_l1Injected =>
                    "[压缩提示] 上下文较长（首次提醒）。如果当前话题不重要（如闲聊），可以调用 `compress` 工具压缩对话历史。例如 `compress` 会保留最近的对话并生成摘要。",
                CompressionTier.L2 =>
                    "[压缩提示] 上下文已较长，请尽快调用 `compress` 工具压缩上下文，腾出空间。",
                CompressionTier.L3 =>
                    "⚠ 这是压缩前最后一轮对话，本轮结束后将强制压缩。请简要告知用户。",
                _ => null
            };
        }

        public void MarkL1Injected() => _l1Injected = true;

        public async Task CompressAsync(List<Message> history, Action<string, List<Message>> onComplete)
        {
            if (_compressing) return;
            _compressing = true;
            try
            {
                using var span = Signal.Open(LogGroup.Engine, "compress",
                    new { historyCount = history.Count, tier = _currentTier.ToString() });

                var retained = new List<Message>();
                int tokenCount = 0;
                for (int i = history.Count - 1; i >= 0; i--)
                {
                    var t = (history[i].Content?.Length ?? 0) / 3;
                    if (retained.Count >= _config.CompressRetainedMessageCount
                        || tokenCount + t > _config.CompressRetainedMaxTokens)
                        break;
                    retained.Insert(0, history[i]);
                    tokenCount += t;
                }

                var toCompress = history.Take(history.Count - retained.Count).ToList();
                if (toCompress.Count > 0
                    && toCompress.Sum(m => (m.Content?.Length ?? 0)) / 3 >= _config.CompressMinTokens)
                {
                    _currentSummary = await _summarizer.SummarizeContextAsync(toCompress, _currentSummary);
                }

                _l1Injected = false;
                span.SetCloseDetail(new { compressedCount = toCompress.Count, retainedCount = retained.Count });
                onComplete(_currentSummary ?? "", retained);
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "压缩失败", new { error = ex.Message });
            }
            finally
            {
                _compressing = false;
            }
        }

        /// <summary>同步压缩（L3 硬保底）。</summary>
        public Task CompressSyncAsync(List<Message> history, Action<string, List<Message>> onComplete)
            => CompressAsync(history, onComplete);
    }

    internal enum CompressionTier { None, L1, L2, L3 }
}
```

- [ ] **Step 2: Write CompressTool.cs**

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Models;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    internal class CompressTool : ITool
    {
        private readonly CompressionTierModule _compression;
        private readonly List<Message> _history;
        private readonly System.Action<string, List<Message>> _onComplete;

        public string Name => "compress";
        public string Description => "压缩当前对话历史。保留最近对话并生成摘要，释放上下文空间。";
        public bool ExpressAvailable => true;
        public ToolType Type => ToolType.Core;
        public List<ToolParameter> Parameters => new();
        public bool GetContinueLoop() => true;

        public CompressTool(CompressionTierModule compression, List<Message> history,
            System.Action<string, List<Message>> onComplete)
        {
            _compression = compression;
            _history = history;
            _onComplete = onComplete;
        }

        public async Task<ToolResult> ExecuteAsync(List<string> inputs, string? toolUseId = null)
        {
            await _compression.CompressAsync(new List<Message>(_history), _onComplete);
            return ToolResult.Success("压缩完成。", toolUseId);
        }
    }
}
```

- [ ] **Step 3: Build and commit**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && dotnet build
```
```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && git add AgentCoreProcessor/Engine/Modules/CompressionTierModule.cs AgentCoreProcessor/Tool/Core/CompressTool.cs && git commit -m "feat: add three-tier compression module and compress tool"
```

---

### Task 5: ChannelContextPersistence

**Files:**
- Create: `AgentCoreProcessor/Engine/Worker/ChannelContextPersistence.cs`

- [ ] **Step 1: Write ChannelContextPersistence.cs**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCoreProcessor.Models;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    internal class ChannelContextPersistence
    {
        private const int FormatVersion = 1;
        private readonly string _filePath;

        public ChannelContextPersistence(int channelId)
        {
            var dir = Path.Combine(Config.PathConfig.StoragePath, "ChannelContexts");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, $"channel_{channelId}.json");
        }

        /// <summary>
        /// Append one round (user+assistant messages) to existing context file.
        /// Loads current state, appends, atomically writes back.
        /// </summary>
        public void AppendRound(List<Message> userMsgs, List<Message> asstMsgs)
        {
            var (summary, mode, rounds) = LoadContext();
            rounds.Add(userMsgs.Concat(asstMsgs).ToList());
            SaveContext(summary, mode, rounds);
        }

        /// <summary>Save compression result: replace summary and rounds.</summary>
        public void SaveCompressionResult(string summary, List<Message> retained, string mode)
        {
            var rounds = new List<List<Message>>();
            for (int i = 0; i < retained.Count; i += 2)
            {
                var pair = new List<Message> { retained[i] };
                if (i + 1 < retained.Count) pair.Add(retained[i + 1]);
                rounds.Add(pair);
            }
            SaveContext(summary, mode, rounds);
        }

        /// <summary>Load context: (summary, mode, rounds). Each round is a flat list of messages.</summary>
        public (string? Summary, string? Mode, List<List<Message>> Rounds) LoadContext()
        {
            if (!File.Exists(_filePath))
                return (null, "working", new List<List<Message>>());

            try
            {
                var json = File.ReadAllText(_filePath);
                dynamic? wrapper = JsonConvert.DeserializeObject(json);
                if (wrapper == null)
                    return (null, "working", new List<List<Message>>());

                int? version = wrapper.FormatVersion;
                if (version == null || version < FormatVersion)
                {
                    File.Delete(_filePath);
                    return (null, "working", new List<List<Message>>());
                }

                string? summary = wrapper.Summary;
                string? mode = wrapper.State?.Mode ?? "working";

                var rounds = new List<List<Message>>();
                if (wrapper.Rounds != null)
                {
                    foreach (var round in wrapper.Rounds)
                    {
                        var msgs = new List<Message>();
                        if (round.User != null)
                            msgs.AddRange(DeserializeMessages(round.User));
                        if (round.Assistant != null)
                            msgs.AddRange(DeserializeMessages(round.Assistant));
                        if (msgs.Count > 0) rounds.Add(msgs);
                    }
                }
                return (summary, mode, rounds);
            }
            catch { return (null, "working", new List<List<Message>>()); }
        }

        private void SaveContext(string? summary, string? mode, List<List<Message>> rounds)
        {
            try
            {
                var data = new
                {
                    FormatVersion,
                    UpdatedAt = DateTime.Now,
                    Summary = summary,
                    State = new { Mode = mode ?? "working" },
                    Rounds = rounds.Select(r => new
                    {
                        User = r.Where(m => m.Role == "user").ToList(),
                        Assistant = r.Where(m => m.Role == "assistant").ToList()
                    }).ToList()
                };
                var json = JsonConvert.SerializeObject(data, Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch { }
        }

        private List<Message>? DeserializeMessages(dynamic obj)
            => JsonConvert.DeserializeObject<List<Message>>(JsonConvert.SerializeObject(obj));
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && dotnet build
```
```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && git add AgentCoreProcessor/Engine/Worker/ChannelContextPersistence.cs && git commit -m "feat: add ChannelContextPersistence — per-channel JSON atomic write"
```

---

### Task 6: SystemEngine refactor — Gate + Agent + IAgentHost + 3-tier compression

**Files:**
- Modify: `AgentCoreProcessor/Engine/System/SystemEngine.cs`
- Modify: `AgentCoreProcessor/Engine/System/ContextPersistence.cs`
- Modify: `AgentCoreProcessor/Engine/System/ContextCompressionModule.cs`

SystemEngine is already closest to target (stacked context, append history, persistence). Key changes:
1. Gate replaces LoopGate (delegate-driven)
2. Agent replaces inline RunAgentLoopAsync
3. IAgentHost implemented
4. CompressionTierModule replaces single-threshold check
5. Remove old inner loop code (~100 lines)

- [ ] **Step 1: Read current SystemEngine.cs to identify exact change points**

Current key lines:
- Line 34: `private readonly LoopGate gate = new();` → change to `private readonly Gate gate;`
- Lines 306-399: `RunAgentLoopAsync` → replace with Agent
- Lines 423-455: `CompressContextAsync` → replace with CompressionTierModule
- Lines 463-483: `BuildFullMessages` → Agent builds messages internally, remove
- Lines 200-202: `await gate.WaitAsync(...)` → Gate.RunAsync handles this

- [ ] **Step 2: Modify constructor to add new fields, replace LoopGate**

```csharp
// Add these fields (after existing field declarations):
private readonly Gate gate;
private readonly Agent agent;
private readonly AgentConfig agentConfig;
private readonly CompressionTierModule compressionTierModule;

// In constructor body, after existing modules init (line ~124),
// add after the existing "modules = new List<EngineModule> { ... }" block:

// Agent config
agentConfig = new AgentConfig
{
    MaxRounds = MaxRoundsPerWake,
    // Use existing constants from SystemEngine:
    // MaxContextTokens = 80000, SoftThresholdPercent = 60, HardThresholdPercent = 85
    CompressL1Tokens = MaxContextTokens * SoftThresholdPercent / 100,  // 48000
    CompressL2Tokens = MaxContextTokens * 70 / 100,                    // 56000
    CompressL3Tokens = MaxContextTokens * HardThresholdPercent / 100,  // 68000
    CompressMinTokens = 5000,
    CompressRetainedMessageCount = 10,
    CompressRetainedMaxTokens = 2000
};

// Compression module
compressionTierModule = new CompressionTierModule(agentConfig,
    () => conversationHistory,
    () =>
    {
        // L3 sync callback: block and compress
        compressionTierModule.CompressSyncAsync(conversationHistory,
            (summary, retained) =>
            {
                compressionModule.SetSummary(summary);
                conversationHistory.Clear();
                conversationHistory.AddRange(retained);
                RecalculateTokens();
                persistence.SaveSummaryAndClearContext(summary);
                foreach (var msg in conversationHistory)
                {
                    persistence.AppendRound(
                        msg.Role == "user" ? new List<Message> { msg } : new List<Message>(),
                        msg.Role == "assistant" ? new List<Message> { msg } : new List<Message>());
                }
            }).GetAwaiter().GetResult();
    });
compressionTierModule.SetSummary(compressionModule.GetSummary());

// Register compress tool
ToolRegistry.Register(new CompressTool(compressionTierModule, conversationHistory,
    (summary, retained) =>
    {
        compressionModule.SetSummary(summary);
        conversationHistory.Clear();
        conversationHistory.AddRange(retained);
        RecalculateTokens();
        persistence.SaveSummaryAndClearContext(summary);
        foreach (var msg in conversationHistory)
            persistence.AppendRound(
                msg.Role == "user" ? new List<Message> { msg } : new List<Message>(),
                msg.Role == "assistant" ? new List<Message> { msg } : new List<Message>());
    }));

// Agent
agent = new Agent(this, agentCore, agentConfig, GetAuthorizedTools());

// Gate (instead of LoopGate)
gate = new Gate(ctx.EventBus);
gate.ShouldActivate = () => Task.FromResult(true);
gate.ExecuteAsync = ExecuteSystemCycleAsync;
```

- [ ] **Step 3: Implement IAgentHost on SystemEngine**

Add to class declaration: `internal class SystemEngine : ISubEngine, IAgentHost`

Add methods:
```csharp
Task<List<Message>?> IAgentHost.BuildStartInjectAsync()
{
    return Task.FromResult<List<Message>?>(null);
}

Task<List<Message>?> IAgentHost.BuildRoundInjectAsync()
{
    var msgs = new List<Message>();
    var mainMsg = BuildCurrentTurnMsg();
    msgs.Add(mainMsg);

    // Compression tier hint
    var compressText = compressionTierModule.GetInjectText(estimatedTokens);
    if (!string.IsNullOrEmpty(compressText))
    {
        msgs.Add(new Message { Role = "user", Content = compressText });
        if (compressionTierModule.CurrentTier == CompressionTier.L1)
            compressionTierModule.MarkL1Injected();
    }
    return Task.FromResult<List<Message>?>(msgs);
}
```

- [ ] **Step 4: Add ExecuteSystemCycleAsync (replaces old RunAsync body)**

```csharp
private async Task ExecuteSystemCycleAsync(CancellationToken ct)
{
    // Health check
    if ((DateTime.Now - lastSleepCheck).TotalMinutes >= 5)
    {
        await PerformHealthCheckAsync();
        lastSleepCheck = DateTime.Now;
    }

    // Collect events
    var tasks = DrainTasks();
    var notifications = DrainNotifications();
    var scheduledEvents = DrainScheduledEvents();
    var pendingDelegations = ctx.Delegations.GetPendingForEvaluation();
    var retryDelegations = ctx.Delegations.GetRetryPending();

    using var iterSignal = Signal.Begin(LogGroup.Engine, "system:main", "系统循环轮次", new
    {
        tasks = tasks.Count, notifications = notifications.Count,
        scheduled = scheduledEvents.Count, delegations = pendingDelegations.Count,
        retryDelegations = retryDelegations.Count
    });

    // Fill PendingEventsModule
    pendingEventsModule.SetPendingEvents(tasks, notifications, scheduledEvents, lastRoundNoAction);
    pendingEventsModule.SetPendingDelegations(pendingDelegations);
    pendingEventsModule.SetRetryPendingDelegations(retryDelegations);

    // Agent loop
    Interlocked.Exchange(ref _busyFlag, 1);
    lastRoundNoAction = false;
    try
    {
        await componentHost.OnActivatedAsync();
        await componentHost.OnBeforeInvokeAsync();
        await agent.RunAsync(ct);
        await componentHost.OnAfterInvokeAsync();
        consecutiveFailures = 0;
        lastRoundNoAction = agent.StopReason == AgentStopReason.Completed;
        SaveModuleState();
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        consecutiveFailures++;
        totalErrorCount++;
        lastErrorTime = DateTime.Now;
        lastErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
        if (consecutiveFailures >= MaxConsecutiveFailures)
        {
            var backoff = BackoffSeconds[Math.Min(consecutiveFailures - 1, BackoffSeconds.Length - 1)];
            await Task.Delay(TimeSpan.FromSeconds(backoff), ct);
        }
    }
    finally
    {
        Interlocked.Exchange(ref _busyFlag, 0);
        lastRoundCalls = null;
        lastRoundResults = null;
        await componentHost.OnPauseAsync();
    }
}
```

- [ ] **Step 5: Replace RunAsync body — remove old while loop, use Gate.RunAsync**

Replace lines ~198-280 (the `while (!ct.IsCancellationRequested)` block and its contents) with:
```csharp
try
{
    await gate.RunAsync(ct);
}
catch (OperationCanceledException) { }
catch (Exception ex) { /* fatal error as before */ }
```

Keep signal lifecycle span, componentHost init, and shutdown/finally blocks.

- [ ] **Step 6: Remove old methods**

Delete these methods from SystemEngine:
- `RunAgentLoopAsync` (lines 306-403)
- `CompressContextAsync` (lines 423-455)
- `BuildFullMessages` (lines 463-483)
- `BuildAssistantMsg` (lines 608-653)
- `toolCallText` (lines 655-656)

Keep:
- `BuildCurrentTurnMsg` (lines 486-563) — still used in IAgentHost
- `AppendToHistory` — simplified, may remove if Agent manages history
- `DrainTasks`, `DrainNotifications`, `DrainScheduledEvents`
- `SaveModuleState`, `GetAuthorizedTools`
- All sleep/permission/sub-agent methods

- [ ] **Step 7: Adjust AppendToHistory**

Old AppendToHistory was called from RunAgentLoopAsync. Now Agent manages _history internally. Remove `persistence.AppendRound` from AppendToHistory — persistence now happens either on compression or when the engine saves context explicitly. For now, keep a simpler version:

```csharp
private void PersistAfterRound()
{
    // Persist after each Agent round to disk
    // Extract last round from agent.History and write
    var history = agent.History;
    if (history.Count >= 2)
    {
        var lastUser = history[history.Count - 2];
        var lastAsst = history[history.Count - 1];
        if (lastUser.Role == "user" && lastAsst.Role == "assistant")
        {
            persistence.AppendRound(
                new List<Message> { lastUser },
                new List<Message> { lastAsst });
        }
    }
}
```

Call this after `await agent.RunAsync(ct)` in ExecuteSystemCycleAsync.

- [ ] **Step 8: Build and fix compilation errors**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && dotnet build 2>&1
```

Likely issues to fix:
- `ToolRegistry.Register` may not exist — check if tools need different registration path
- `CompressionTier` enum in module namespace — ensure using directive
- `SummarizationCore` — verify class exists and namespace is correct
- `history` → `agent.History` references in ExecuteSystemCycleAsync

Fix each error and rebuild until clean.

- [ ] **Step 9: Run in test mode to verify**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && dotnet run
```
Expected: SystemEngine starts, processes tasks, calls model, executes tools. Verify in WebUI at localhost:5000.

- [ ] **Step 10: Commit**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && git add AgentCoreProcessor/Engine/System/SystemEngine.cs AgentCoreProcessor/Engine/System/ContextPersistence.cs AgentCoreProcessor/Engine/System/ContextCompressionModule.cs && git commit -m "refactor: SystemEngine uses Gate+Agent+IAgentHost+3-tier compression"
```

---

### Task 7: ChannelEngine refactor — Stacked context + Gate + Agent + persistence

**Files:**
- Modify: `AgentCoreProcessor/Engine/Worker/ChannelEngine.cs`

The biggest change in Phase 1. ChannelEngine moves from per-round XML rebuild to stacked context.

**Key design decisions:**
- Express mode: one-shot, direct Core call, **no Agent** (tools fire-and-forget, results don't feed back)
- Working mode: multi-round, **Agent** handles the loop
- Fixed prefix: tool definitions + identity + rules, generated once when mode switches
- History: user/assistant pairs, Agent manages internally
- Working session state: `lastRoundCalls/Results` → Agent's `LastRoundCalls/Results`
- Impulse, buffer, participants, interceptors, watch rules all stay as engine-level pre-processing
- Memory injection moves to IAgentHost (BuildStartInjectAsync)

- [ ] **Step 1: Read current ChannelEngine.cs sections to understand exact changes needed**

Critical transition points (from current code):
- Lines 511-656: `PrepareContextAsync` — gate evaluation, impulse check → becomes `ShouldActivate` delegate
- Lines 659-697: `AssembleRoundContextAsync` — XML context building → removed, replaced by stacked context
- Lines 700-860: `BuildPromptMessages` — per-round message assembly → Agent does this internally
- Lines 863-951: `ProcessResponseAsync` — text/tool handling → Agent for Working, simplified for Express
- Lines 954-1011: `DecideNext` — continue/sleep/mode switch → Agent stop reason + engine logic

- [ ] **Step 2: Add new fields to ChannelEngine**

```csharp
// Working mode state (replacing scattered fields):
private Gate? gate;
private Agent? agent;
private AgentConfig agentConfig;
private ChannelContextPersistence? persistence;
private CompressionTierModule? compressionTierModule;

// Stacked context fields:
private string? fixedPrefix;       // Generated once per mode, never changes
private string? contextSummary;    // From compression

// Removed fields (no longer needed):
// - currentContextXml (XML rebuilt every round)
// - currentImageEmbeds (passed through Message ContentParts)
// - lastRoundCalls, lastRoundResults (Agent manages)
// - isInWorkingSession (Agent manages lifecycle)
// - PromptBuilder promptBuilder (not needed, Agent formats)
// - ContextBuilder contextBuilder (not needed, no XML)
```

- [ ] **Step 3: Modify constructor — initialize Gate and persistence**

```csharp
// In constructor, after existing field initializations (~line 162-188):

// Persistence
persistence = new ChannelContextPersistence(channelId);
agentConfig = new AgentConfig
{
    MaxRounds = 20,
    CompressL1Tokens = 30000,
    CompressL2Tokens = 50000,
    CompressL3Tokens = 70000,
    CompressMinTokens = 5000,
    CompressRetainedMessageCount = 6,
    CompressRetainedMaxTokens = 2000
};

// Gate
gate = new Gate(ctx.EventBus);
gate.ShouldActivate = PrepareContextAsync;  // Reuse existing impulse check logic
gate.ExecuteAsync = ExecuteChannelCycleAsync;

// Restore persisted context
RestoreContext();
```

- [ ] **Step 4: Add RestoreContext method**

```csharp
private void RestoreContext()
{
    if (persistence == null) return;
    var (summary, mode, rounds) = persistence.LoadContext();
    contextSummary = summary;
    if (mode == "working")
        isWorkingMode = true;

    // Rebuild agent with restored history
    if (rounds.Count > 0)
    {
        EnsureAgent();
        foreach (var round in rounds)
            foreach (var msg in round)
                agent!.AddToHistory(msg);
    }
}
```

- [ ] **Step 5: Add BuildFixedPrefix — called when mode changes or on first use**

```csharp
private string BuildFixedPrefix()
{
    var sb = new System.Text.StringBuilder();

    // Tool definitions (full set, not filtered by mode)
    var authorizedTools = new HashSet<string>
    {
        "speak", "send_media", "thinking_notes", "memory", "pinboard", "retain_list",
        "task_management", "mark_review_hint", "alert", "wait", "read_file", "write_file",
        "delegate_task", "adapter_action", "view_image", "get_image_text", "compress"
    };
    var filter = new Func<ITool, bool>(t => authorizedTools.Contains(t.Name));

    if (agentCore.UseNativeTools)
    {
        // Native mode: add context only, tools via API
        sb.AppendLine("[系统配置]");
        var botId = ctx.Adapters.GetBotPlatformId("qq");
        if (!string.IsNullOrEmpty(botId))
            sb.AppendLine($"身份信息：你的QQ号是 {botId}。");
        sb.AppendLine("[图片标记说明] 上下文中的 <img/> 标记表示用户发送的图片。desc/text 属性为自动生成的摘要，仅供快速参考。涉及具体内容时请使用工具查看原图或获取完整文字。");
    }
    else
    {
        sb.AppendLine(ToolRegistry.GenerateDescriptions(authorizedTools: authorizedTools, filter: filter));
        var botId = ctx.Adapters.GetBotPlatformId("qq");
        if (!string.IsNullOrEmpty(botId))
            sb.AppendLine($"\n身份信息：你的QQ号是 {botId}。");
        sb.AppendLine("\n[图片标记说明] 上下文中的 <img/> 标记表示用户发送的图片。desc/text 属性为自动生成的摘要，仅供快速参考。涉及具体内容时请使用工具查看原图或获取完整文字。");
    }

    return sb.ToString();
}

private void EnsureAgent()
{
    if (agent != null) return;
    fixedPrefix = BuildFixedPrefix();

    var authorized = new HashSet<string>(/* same tool whitelist as BuildFixedPrefix */);
    agent = new Agent(this, agentCore, agentConfig, authorized);

    if (fixedPrefix != null)
        agent.AddToHistory(new Message { Role = "user", Content = fixedPrefix });

    if (!string.IsNullOrEmpty(contextSummary))
        agent.AddToHistory(new Message { Role = "user", Content = $"[上下文摘要]\n{contextSummary}" });
}
```

- [ ] **Step 6: Implement IAgentHost on ChannelEngine**

Add to class declaration: `internal class ChannelEngine : ISubEngine, IAgentHost`

```csharp
Task<List<Message>?> IAgentHost.BuildStartInjectAsync()
{
    // New messages from buffer (one-time injection)
    var batch = CollectBuffer();
    if (batch == null || batch.Count == 0) return Task.FromResult<List<Message>?>(null);

    var msgs = new List<Message>();
    var sb = new System.Text.StringBuilder("<新消息>\n");

    // Format new messages
    foreach (var (msg, sc) in batch)
    {
        var name = sc.Person.Name ?? sc.User.PlatformId;
        sb.AppendLine($"{name}: {msg.Content}");
    }
    sb.AppendLine("</新消息>");

    // Memory injection (per-person, cached)
    if (currentLastSc != null && currentLastMsg != null)
    {
        var memoryText = FormatMemoryForInjection(currentLastSc, currentLastMsg.Content);
        if (!string.IsNullOrEmpty(memoryText))
            sb.AppendLine($"\n[相关记忆]\n{memoryText}");
    }

    // Interceptor injections
    if (interceptorInjections.Count > 0)
    {
        sb.AppendLine("\n[系统提示]");
        foreach (var inj in interceptorInjections)
            sb.AppendLine(inj);
    }

    msgs.Add(new Message { Role = "user", Content = sb.ToString() });

    // Component prompt sections
    AddComponentInjections(msgs);

    return Task.FromResult<List<Message>?>(msgs);
}

Task<List<Message>?> IAgentHost.BuildRoundInjectAsync()
{
    var msgs = new List<Message>();

    // Module injections (LoopControl, etc.)
    foreach (var module in modules.OrderBy(m => m.PromptPriority))
    {
        var section = module.BuildPromptSection(
            isWorkingMode ? EngineMode.Working : EngineMode.Express);
        if (!string.IsNullOrEmpty(section))
            msgs.Add(new Message { Role = "user", Content = section });
    }

    // Compression tier hint
    if (compressionTierModule != null && agent != null)
    {
        var estTokens = agent.History.Sum(m => (m.Content?.Length ?? 0)) / 3;
        var text = compressionTierModule.GetInjectText(estTokens);
        if (!string.IsNullOrEmpty(text))
        {
            msgs.Add(new Message { Role = "user", Content = text });
            if (compressionTierModule.CurrentTier == CompressionTier.L1)
                compressionTierModule.MarkL1Injected();
        }
    }

    // Component injections (per-round)
    AddComponentInjections(msgs);

    return Task.FromResult<List<Message>?>(msgs);
}

private void AddComponentInjections(List<Message> msgs)
{
    if (componentHost != null)
    {
        var groups = ToolListFormatter.CollectGroups(componentHost, ctx.GlobalComponentHost);
        var overview = ToolListFormatter.BuildToolOverviewSection(groups);
        if (overview != null)
            msgs.Add(new Message { Role = "user", Content = overview });

        var sections = componentHost.BuildPromptSections();
        foreach (var s in sections)
            msgs.Add(new Message { Role = "user", Content = s });

        var globalSections = ctx.GlobalComponentHost?.BuildPromptSections(
            new LoopInfo(channelId.ToString(), "channel")) ?? new();
        foreach (var s in globalSections)
            msgs.Add(new Message { Role = "user", Content = s });
    }
}
```

- [ ] **Step 7: Add ExecuteChannelCycleAsync (replaces RunAsync's inner block)**

```csharp
private async Task ExecuteChannelCycleAsync(CancellationToken ct)
{
    Interlocked.Exchange(ref _busyFlag, 1);
    try
    {
        await componentHost.OnBeforeInvokeAsync();

        if (isWorkingMode)
        {
            // Working mode: use Agent for multi-round
            EnsureAgent();

            // Register compress tool
            if (compressionTierModule == null)
            {
                compressionTierModule = new CompressionTierModule(agentConfig,
                    () => agent.History,
                    () =>
                    {
                        compressionTierModule.CompressSyncAsync(agent.History,
                            (summary, retained) =>
                            {
                                contextSummary = summary;
                                agent.ClearHistory();
                                agent.AddToHistory(new Message { Role = "user", Content = fixedPrefix! });
                                if (!string.IsNullOrEmpty(summary))
                                    agent.AddToHistory(new Message { Role = "user", Content = $"[上下文摘要]\n{summary}" });
                                foreach (var m in retained) agent.AddToHistory(m);
                                persistence?.SaveCompressionResult(summary, retained,
                                    isWorkingMode ? "working" : "express");
                            }).GetAwaiter().GetResult();
                    });
                ToolRegistry.Register(new CompressTool(compressionTierModule, agent.History,
                    (summary, retained) =>
                    {
                        contextSummary = summary;
                        agent.ClearHistory();
                        agent.AddToHistory(new Message { Role = "user", Content = fixedPrefix! });
                        if (!string.IsNullOrEmpty(summary))
                            agent.AddToHistory(new Message { Role = "user", Content = $"[上下文摘要]\n{summary}" });
                        foreach (var m in retained) agent.AddToHistory(m);
                        persistence?.SaveCompressionResult(summary, retained,
                            isWorkingMode ? "working" : "express");
                        gate?.Signal();  // Re-evaluate with new context
                    }));
            }

            await agent.RunAsync(ct);

            // Persist after agent finishes
            if (persistence != null && agent.History.Count > 0)
            {
                var rounds = new List<List<Message>>();
                for (int i = 0; i < agent.History.Count; i += 2)
                {
                    var pair = new List<Message> { agent.History[i] };
                    if (i + 1 < agent.History.Count)
                        pair.Add(agent.History[i + 1]);
                    rounds.Add(pair);
                }
                persistence.SaveContext(contextSummary,
                    isWorkingMode ? "working" : "express", rounds);
            }

            // Check agent stop reason
            if (agent.StopReason == AgentStopReason.WaitRequested)
            {
                EndWorkingSession();
            }
            else if (agent.StopReason == AgentStopReason.MaxRounds)
            {
                loopControlModule.AdvanceRound(speakModule!.HadSpeakThisRound);
                if (!loopControlModule.IsMaxRoundsReached)
                    gate?.Signal();  // Continue
                else
                    EndWorkingSession();
            }
            else
            {
                EndWorkingSession();
            }
        }
        else
        {
            // Express mode: direct Core call (no Agent)
            await ProcessExpressAsync(ct);
        }

        consecutiveFailures = 0;
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        consecutiveFailures++;
        totalErrorCount++;
        lastErrorTime = DateTime.Now;
        lastErrorMessage = $"{ex.GetType().Name}: {ex.Message}";
        Signal.Error(LogGroup.Engine, "处理异常",
            new { error = ex.GetType().Name, message = ex.Message, consecutiveFailures });

        if (currentLastMsg != null && consecutiveFailures <= 1)
        {
            try
            {
                await ctx.Adapters.SendMessageAsync(currentLastMsg.Platform, new OutgoingMessage
                {
                    ChannelId = currentLastMsg.ChannelId,
                    Content = $"[错误] 处理消息时发生异常：{ex.Message}"
                });
            }
            catch { }
        }

        if (consecutiveFailures >= ChannelMaxConsecutiveBeforeBackoff)
        {
            Signal.Warn(LogGroup.Engine, "连续失败退避",
                new { channelId, consecutiveFailures, backoffSeconds = ChannelBackoffSeconds });
            await Task.Delay(TimeSpan.FromSeconds(ChannelBackoffSeconds), ct);
        }

        isInWorkingSession = false;
    }
    finally
    {
        if (!isInWorkingSession)
        {
            Interlocked.Exchange(ref _busyFlag, 0);
            Interlocked.Exchange(ref _completionTicks, DateTime.Now.Ticks);
            await componentHost.OnPauseAsync();
        }
    }
}

private async Task ProcessExpressAsync(CancellationToken ct)
{
    var batch = CollectBuffer();
    var prepareResult = await PrepareContextAsync(batch);
    if (!prepareResult) return;

    // Build messages for single-shot Express call
    var messages = new List<Message>();
    if (fixedPrefix == null) fixedPrefix = BuildFixedPrefix();
    messages.Add(new Message { Role = "user", Content = fixedPrefix! });

    // Inject new messages
    var inject = await ((IAgentHost)this).BuildStartInjectAsync();
    if (inject != null) messages.AddRange(inject);

    var roundInject = await ((IAgentHost)this).BuildRoundInjectAsync();
    if (roundInject != null) messages.AddRange(roundInject);

    // Call model
    var mode = EngineMode.Express;
    using var modelSpan = Signal.Open(LogGroup.Model, "AI模型调用",
        new { mode = "Express", channelId, messageCount = messages.Count }));
    var output = await agentCore.InvokeAsync(messages, mode);
    modelSpan.SetCloseDetail(new { isText = output.IsText, hasToolCalls = output.HasToolCalls });

    // Process Express output (fire-and-forget tools, escalate detection)
    if (output.IsText)
    {
        var text = output.Text!;
        text = await ProcessPokeMarkers(text, currentLastMsg!);
        if (!string.IsNullOrEmpty(text))
            await SendSegmentsAsync(text, currentLastMsg!, currentLastSc!, currentParticipantSnapshot!);

        if (output.HasToolCalls && output.ToolCalls != null)
        {
            var executor = new ToolExecutor(authorizedTools: null);
            await executor.ExecuteAsync(output.ToolCalls);

            var escalate = output.ToolCalls.FirstOrDefault(c => c.Tool == "escalate");
            if (escalate != null)
            {
                escalationReason = escalate.Inputs.FirstOrDefault();
                isWorkingMode = true;
                Signal.Event(LogGroup.Engine, "模式切换",
                    new { channelId, from = "Express", to = "Working", reason = escalationReason ?? "工具调用" });
                gate?.Signal();
            }
        }
    }

    TrackMemoryExtraction(batch!, currentLastSc!);
    await IncrementDailyProgressAsync(currentLastSc!.Person);
    impulseTracker.ApplyPostResponseUpdate();
}
```

- [ ] **Step 8: Replace RunAsync — use Gate.RunAsync**

Replace the `while (IsAlive)` loop body (lines 278-429) with:
```csharp
public async Task RunAsync()
{
    var lifeCtx = Signal.Continue(...);

    WireModuleCallbacks();
    componentHost = new ComponentHost(...);
    await componentHost.InitAsync();

    try
    {
        await gate!.RunAsync(ct);  // Gate handles the loop
    }
    catch (OperationCanceledException) { }
    finally
    {
        if (componentHost != null)
            await componentHost.ShutdownAsync(ShutdownReason.Destroy);
        foreach (var m in modules) m.Reset();
        lifeCtx.Close(...);
    }
}
```

Keep the signal lifecycle span, WireModuleCallbacks, componentHost init. Remove the old while loop and all its session context tracking.

- [ ] **Step 9: Remove old methods, keep channel-specific ones**

Remove:
- `AssembleRoundContextAsync` (lines 659-697)
- `BuildPromptMessages` (lines 700-860)
- `ProcessResponseAsync` (lines 863-951)
- `DecideNext` (lines 954-1011)
- `EndWorkingSession` — keep but simplify
- `PromptBuilder promptBuilder` field (line 71)
- `ContextBuilder contextBuilder` field (line 72)
- `currentContextXml`, `currentImageEmbeds`, `lastRoundCalls`, `lastRoundResults` fields
- `isInWorkingSession` field — still needed for busy/idle tracking

Keep (unchanged):
- `EnqueueMessage`, `ScheduleBufferSignal` (buffer management)
- `CollectBuffer` (returns batch for injection)
- `PrepareContextAsync` (now the ShouldActivate delegate)
- `CollectImagePaths`, `ResolveImagePresentationAsync` (image handling)
- `TrackMemoryExtraction`, `GetCachedMemoryAsync`, `FetchMemoryAsync` (memory)
- `SendSegmentsAsync`, `ProcessPokeMarkers`, `ParseBotOutput` (message sending)
- `HandleAlertAsync`, `IncrementDailyProgressAsync`
- `CheckWatchRulesAsync`, `GetWatchRules`, `UpdateWatchRules`
- `SetInterceptors`, `InjectNotification`, `DrainSystemNotifications`
- `OnEvent`, `RequestStop`, `GetSnapshot`
- All module field declarations and InitModules
- `WireModuleCallbacks`
- `authorizedTools`, `currentProfileName`
- `LoadConfig`, `CollectBuffer`, `ParseBotOutput`

- [ ] **Step 10: Build and fix compilation errors**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && dotnet build 2>&1
```

Likely issues:
- Type mismatches between old and new API
- Missing field references in kept methods
- `SpeakModule.OnSpeak` callbacks reference old state — update to use Agent state
- `loopControlModule` references in old methods — update to use Agent.TotalRounds
- `currentLastMsg/currentLastSc/currentParticipantSnapshot` — these are set in PrepareContextAsync, still available

Fix each error and rebuild.

- [ ] **Step 11: Run in test mode to verify**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && dotnet run
```
Expected: ChannelEngine processes messages in Express mode, escalates to Working, runs multi-round Agent loop. Verify in WebUI.

- [ ] **Step 12: Commit**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && git add AgentCoreProcessor/Engine/Worker/ChannelEngine.cs && git commit -m "refactor: ChannelEngine uses stacked context + Gate + Agent + persistence"
```

---

### Task 8: End-to-end integration test + docs

**Files:**
- Modify: `CLAUDE.md` (update key paths)
- Modify: `docs/architecture-map.md` (if exists, update engine descriptions)

- [ ] **Step 1: Full build**

```bash
taskkill /IM AgentCoreProcessor.exe /F 2>/dev/null
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && dotnet build
```
Expected: Clean build, zero errors.

- [ ] **Step 2: Dry run with --test mode**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && dotnet run -- --test
```
Expected: Both engines start, SystemEngine processes tasks, ChannelEngine processes test messages.

- [ ] **Step 3: Verify WebUI**

Navigate to localhost:5000. Verify:
- SystemEngine shows correct state
- ChannelEngine shows correct channel count
- Logs page shows new signal spans (gate:activate, agent:loop, agent:round, core:invoke, agent:tools)

- [ ] **Step 4: Update CLAUDE.md key paths**

Replace the old references:
```
- Agent 循环：Core/WorkingCore.cs
```
→
```
- Gate 骨架：Engine/Core/Gate.cs
- Agent 循环：Engine/Core/Agent.cs
- Agent 宿主：Engine/Core/IAgentHost.cs
```

- [ ] **Step 5: Commit**

```bash
cd E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution && git add CLAUDE.md docs/ && git commit -m "docs: update key paths for engine unification Phase 1"
```

---

## Phase 1 Completion Criteria

- [x] Gate class with EventBus subscription + ForceWake + delegate-driven loop skeleton
- [x] Agent class extracting multi-round loop from both engines
- [x] IAgentHost with BuildStartInjectAsync / BuildRoundInjectAsync
- [x] Three-tier compression (L1/L2/L3) with compress tool
- [x] ChannelContextPersistence (per-channel JSON)
- [x] SystemEngine refactored: Gate + Agent + IAgentHost + 3-tier compression
- [x] ChannelEngine refactored: stacked context + Gate + Agent + IAgentHost + persistence
- [x] Build passes, test mode verified, WebUI operational
- [ ] Module system (IInjectProvider, plugin injection) — deferred to Phase 2
- [ ] DreamEngine refactor — deferred to Phase 2
- [ ] Other engines (Vision/Timer/Task) — deferred to Phase 2
