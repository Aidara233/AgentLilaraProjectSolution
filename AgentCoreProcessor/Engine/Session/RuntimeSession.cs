// AgentCoreProcessor/Engine/Session/RuntimeSession.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 统一会话实现。行为由 CreateSessionSpec 驱动，不分子类。
/// </summary>
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
    private readonly Dictionary<string, IInputPort> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IOutputPort> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    private SessionAgentHost? _host;

    public void SetAgentHost(SessionAgentHost host) => _host = host;

    private ComponentHost? _componentHost;
    private ICurrencyService? _currency;
    private AgentCore? _agentCore;
    private MessageEngine? _messageEngine;
    private object? _initialState;
    private int _roundCount;
    private readonly DateTime _createdAt = DateTime.UtcNow;
    private DateTime _lastActivity = DateTime.UtcNow;
    private CancellationTokenSource? _cts;

    /// <summary>会话完成时触发（正常结束/异常/崩溃）。</summary>
    public event Action<IRuntimeSession>? OnCompleted;

    private const int FreeRounds = 20;
    private const int MaxAgentRounds = 15;

    public RuntimeSession(CreateSessionSpec spec)
    {
        MaxLevel = spec.MaxLevel;
        ParentSessionId = spec.ParentSessionId;
        if (spec.OverrideLimits != null) Limits = spec.OverrideLimits;
        _componentIds.AddRange(spec.ComponentIds);
        _initialState = spec.InitialState;
    }

    // ── 依赖注入 ──

    public void SetCurrency(ICurrencyService currency) => _currency = currency;
    public void SetAgentCore(AgentCore core) => _agentCore = core;
    public void SetMessageEngine(MessageEngine me) => _messageEngine = me;

    /// <summary>
    /// 从 ComponentRegistry 加载指定组件，构建 ComponentHost。
    /// loopId 示例："channel:123"（单频道）或 "session:xxxx"（多端口/无端口），
    /// loopType 从 loopId 提取（"channel"/"session" 等），用于组件白名单查找。
    /// </summary>
    public void InitComponents(string loopId, ModuleBus moduleBus, IServiceProvider services, Action wakeLoop)
    {
        // 从 loopId 提取 loopType（LoopId.ExtractLoopType 已有此逻辑）
        var loopType = LoopId.ExtractLoopType(loopId);
        // 组件白名单使用 "channel" 类型（BasicTools 等都注册在 "channel" 下）
        var componentLoopType = loopType == "session" ? "channel" : loopType;
        _componentHost = new ComponentHost(loopId, componentLoopType, moduleBus, services, wakeLoop);
        _componentHost.InitAsync().GetAwaiter().GetResult();

        RefreshToolList();
    }

    private void RefreshToolList()
    {
        _activeTools.Clear();
        _tools.Clear();
        if (_componentHost == null) return;

        foreach (var tool in _componentHost.GetVisibleTools())
        {
            _tools[tool.Name] = tool;
            _activeTools.Add(tool.Name);
        }
        // 合并 ToolRegistry 独立工具
        foreach (var (name, tool) in ToolRegistry.All)
        {
            if (!_tools.ContainsKey(name))
            {
                _tools[name] = tool;
                _activeTools.Add(name);
            }
        }
    }

    // ── 端口管理 ──

    public void AddInputPort(string name, IInputPort port) => _inputs[name] = port;
    public void AddOutputPort(string name, IOutputPort port) => _outputs[name] = port;

    // ── 组件管理 ──

    public void EnableComponent(string componentId)
    {
        if (_componentIds.Contains(componentId)) return;
        _componentIds.Add(componentId);
        // 运行时加载：实例化组件并注册其工具
    }

    public void DisableComponent(string componentId)
    {
        _componentIds.Remove(componentId);
        RefreshToolList();
    }

    // ── 等级 ──

    public void SetLevel(int newLevel) => MaxLevel = newLevel;

    public bool CheckLifetimeLimits()
    {
        var now = DateTime.UtcNow;
        if (Limits.MaxLifetime > TimeSpan.Zero && now - _createdAt > Limits.MaxLifetime) return false;
        if (Limits.IdleTimeout > TimeSpan.Zero && now - _lastActivity > Limits.IdleTimeout) return false;
        return true;
    }

    public void RequestStop(string reason)
    {
        Status = SessionStatus.Completed;
        _cts?.Cancel();
    }

    public void SetWaitUntil(DateTime? until) => WaitUntil = until;
    public void TouchActivity() => _lastActivity = DateTime.UtcNow;

    // ── Agent 循环 ──

    public async Task RunAsync(CancellationToken ct)
    {
        Status = SessionStatus.Running;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // 等待 AgentHost 初始化（异步构建，最多等 3 秒）
        var hostWait = 0;
        while (_host == null && hostWait < 30)
        {
            await Task.Delay(100, ct);
            hostWait++;
        }

        // 构建初始消息列表：优先用 host 的富上下文，fallback 到裸 InitialState 字符串
        List<Message> messages;
        if (_host != null)
        {
            try
            {
                messages = await _host.BuildStartAsync();
                Console.Error.WriteLine($"[Session {SessionId}] BuildStartAsync: {messages.Count} messages from AgentHost");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Session {SessionId}] BuildStartAsync failed, fallback: {ex.Message}");
                messages = new List<Message>();
                if (_initialState is string initText && !string.IsNullOrEmpty(initText))
                    messages.Add(new Message { Role = "user", Content = initText });
            }
        }
        else
        {
            // AgentHost 未就绪，fallback 到裸 InitialState
            messages = new List<Message>();
            if (_initialState is string initText && !string.IsNullOrEmpty(initText))
                messages.Add(new Message { Role = "user", Content = initText });
            Console.Error.WriteLine($"[Session {SessionId}] AgentHost not ready, using bare InitialState");
        }

        int agentRound = 0;

        try
        {
            while (IsAlive && !ct.IsCancellationRequested)
            {
                // 如果有初始消息，直接进入处理循环；否则等待输入
                if (messages.Count == 0)
                {
                    if (!await WaitForActivationAsync(_cts.Token))
                        break;

                    if (!CheckLifetimeLimits())
                    {
                        Status = SessionStatus.Completed;
                        break;
                    }

                    TouchActivity();
                    SetWaitUntil(null);

                    // 轮次定价
                    _roundCount++;
                    ApplyRoundPricing();

                    // 收集输入，并通过 host 格式化（如有）
                    var drained = DrainInputPortsRaw(ct);
                    if (drained.Count == 0) continue;

                    if (_host != null)
                    {
                        try
                        {
                            // 只把有真实 channelId 的消息传给 BuildRoundAsync（host 只知道注册的频道）
                            var channelDrained = drained.Where(d => d.channelId > 0).ToList();
                            var systemDrained = drained.Where(d => d.channelId <= 0).ToList();

                            messages = channelDrained.Count > 0
                                ? await _host.BuildRoundAsync(channelDrained)
                                : new List<Message>();

                            // 系统注入消息直接追加
                            foreach (var (_, um) in systemDrained)
                                messages.Add(new Message { Role = "user", Content = um.Content });

                            Console.Error.WriteLine($"[Session {SessionId}] BuildRoundAsync: {messages.Count} messages for {channelDrained.Count} channel + {systemDrained.Count} system input(s)");
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[Session {SessionId}] BuildRoundAsync failed, fallback: {ex.Message}");
                            messages = drained.Select(d => new Message { Role = "user", Content = d.msg.Content }).ToList();
                        }
                    }
                    else
                    {
                        // fallback：裸内容
                        messages = drained.Select(d => new Message { Role = "user", Content = d.msg.Content }).ToList();
                    }

                    if (messages.Count == 0) continue;
                }

                // ── Agent 推理 ──
                if (_agentCore == null) break;

                _agentCore.EngineType = "channel"; // 复用 channel 工具白名单
                _agentCore.CurrentModeId = MaxLevel > 0 ? "build" : "plan";
                _agentCore.AdditionalTools = _tools.Values.ToList();
                Console.Error.WriteLine($"[Session {SessionId}] Tools: {string.Join(", ", _tools.Keys)} (EngineType={_agentCore.EngineType}, Mode={_agentCore.CurrentModeId})");

                agentRound++;
                if (agentRound > MaxAgentRounds)
                {
                    await SendOutput($"已达到最大轮次限制（{MaxAgentRounds}），会话即将结束。");
                    break;
                }

                ModelOutput output;
                try
                {
                    output = await _agentCore.InvokeAsync(messages, EngineMode.Working);
                    Console.Error.WriteLine($"[Session {SessionId}] ModelOutput: Text={output.Text?.Length ?? 0}chars, ToolCalls={output.ToolCalls?.Count ?? 0}");
                }
                catch (Exception ex)
                {
                    messages.Add(new Message { Role = "system", Content = $"[错误] 模型调用失败: {ex.Message}" });
                    continue;
                }

                messages.Add(new Message { Role = "assistant", Content = output.Text ?? output.Thinking ?? "" });

                // ── 工具执行 ──
                if (output.HasToolCalls && output.ToolCalls != null && output.ToolCalls.Count > 0)
                {
                    System.Func<string, ITool?> resolver = name =>
                        _componentHost?.TryGetTool(name) ?? ToolRegistry.Get(name);
                    var executor = new ToolExecutor(resolver);

                    var results = await executor.ExecuteAsync(output.ToolCalls);
                    Console.Error.WriteLine($"[Session {SessionId}] ToolResults: {string.Join(", ", output.ToolCalls.Select((c, i) => $"{c.Tool}={results[i].Status}"))}");

                    // assistant 轮：保留 tool_use ContentParts（携带 tool_use_id），与下方 tool_result 严格配对
                    // 替换上面那条空文本 assistant，避免模型看不到自己调用了工具（runaway 根因）
                    messages[messages.Count - 1] = NativeToolMessageFormatter.FormatAssistant(
                        output, _agentCore.UseNativeTools, resolver);

                    // tool_result 轮：一条消息按 tool_use_id 配对全部 call→result
                    messages.Add(NativeToolMessageFormatter.FormatToolResults(
                        output.ToolCalls, results, _agentCore.UseNativeTools));

                    // 副作用 + 停止判定（镜像 Agent.cs 的停循环语义）
                    var stopRequested = false;
                    for (int i = 0; i < output.ToolCalls.Count; i++)
                    {
                        var call = output.ToolCalls[i];
                        var result = results[i];

                        switch (call.Tool)
                        {
                            // speak/send_media 等 OutputOnly 工具自带发送，不在此重发（旧代码 SendOutput(result.Data) 会把消息ID当正文重发）
                            case "deescalate":
                                // deescalate 自身不发消息，这里把理由作为收尾发言发出
                                await SendOutput(result.Data ?? "工作完成，切回轻量模式。");
                                stopRequested = true;
                                break;
                            case "wait":
                                // 分钟数来自第二个参数 timeout_minutes（Inputs[1]），Inputs[0] 是 reason
                                var mins = 5;
                                if (call.Inputs.Count > 1 && int.TryParse(call.Inputs[1], out var parsed))
                                    mins = parsed;
                                SetWaitUntil(DateTime.UtcNow.AddMinutes(mins));
                                stopRequested = true;
                                break;
                            case "switch_mode":
                                // 子模式切换：批次1仅停止突发避免静默无限循环；真正的模式切换待批次2循环重构
                                stopRequested = true;
                                break;
                        }
                    }

                    // 内联 wait（speak 含 [wait] 占位符时 result.RequestWait=true）
                    if (results.Any(r => r.RequestWait))
                        stopRequested = true;

                    if (stopRequested)
                        break;
                }
                else
                {
                    // 无工具调用：模型给了最终回复，结束突发，等待下一轮输入
                    messages.Clear();
                }
            }
        }
        finally
        {
            if (Status == SessionStatus.Running)
                Status = SessionStatus.Completed;
            OnCompleted?.Invoke(this);
        }
    }

    private Task SendOutput(string text)
    {
        if (_messageEngine == null || string.IsNullOrEmpty(text))
            return Task.CompletedTask;

        int channelId = 0;
        foreach (var (_, port) in _inputs)
        {
            if (port is MessageInputPort mp) { channelId = mp.ChannelId; break; }
        }
        if (channelId <= 0)
            return Task.CompletedTask;

        return _messageEngine.SendOutgoing(new EngineOutgoingMessage
        {
            ChannelId = channelId,
            Content = text,
            SentByBot = true
        });
    }


    /// <summary>
    /// 从所有输入端口收集消息，转换为 (channelId, UserMessage) 元组列表。
    /// EngineIncomingMessage → UserMessage：最小化转换（无 SenderName，但含 ChannelId/Content/UserId/PlatformMessageId）。
    /// </summary>
    private List<(int channelId, UserMessage msg)> DrainInputPortsRaw(CancellationToken ct)
    {
        var result = new List<(int channelId, UserMessage msg)>();
        foreach (var (_, port) in _inputs)
        {
            while (port.TryPeek(out var data))
            {
                port.ReceiveAsync(ct).GetAwaiter().GetResult();
                if (data is EngineIncomingMessage em)
                {
                    // 最小化 EngineIncomingMessage → UserMessage 转换
                    // SenderName 暂缺（EngineIncomingMessage 只有 SenderUserId，无 SenderName）
                    var um = new UserMessage
                    {
                        ChannelId = em.ChannelId,
                        UserId = em.SenderUserId,
                        Content = em.Content,
                        PlatformMessageId = em.MessageId,
                        Time = em.Timestamp == default ? DateTime.UtcNow : em.Timestamp,
                        IsFromBot = false,
                        SenderName = "" // 无法从 EngineIncomingMessage 获取，FormatIncomingAsync 将显示空名
                    };
                    result.Add((em.ChannelId, um));
                }
                else if (data is string s)
                {
                    // 纯字符串注入（timeout/signal 等）
                    var um = new UserMessage { Content = s, ChannelId = 0, Time = DateTime.UtcNow };
                    result.Add((0, um));
                }
                else if (data != null)
                {
                    // 其他类型（JSON 序列化）
                    var um = new UserMessage { Content = JsonConvert.SerializeObject(data), ChannelId = 0, Time = DateTime.UtcNow };
                    result.Add((0, um));
                }
            }
        }
        return result;
    }

    private void ApplyRoundPricing()
    {
        if (_roundCount <= FreeRounds || _currency == null) return;
        var price = (decimal)Math.Ceiling((_roundCount - FreeRounds) / 10.0);
        if (_currency.Balance >= price)
        {
            try { _currency.Spend(price, $"Session {SessionId} round {_roundCount}"); }
            catch { /* 扣费失败不中断 */ }
        }
        else
        {
            InjectMessage(new { type = "warning", text = $"余额不足（当前 {_currency.Balance:F0} 币），无法续期。请收尾。" });
        }
    }

    private async Task<bool> WaitForActivationAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // 检查是否有输入
            foreach (var (_, port) in _inputs)
                if (port.TryPeek(out _)) return true;

            // 检查 WaitUntil 是否到期
            if (WaitUntil != null && DateTime.UtcNow >= WaitUntil.Value)
            {
                // 超时激活，注入 timeout 消息
                InjectMessage(new { type = "timeout", wait_reason = "wait timeout expired" });
                return true;
            }

            await Task.Delay(200, ct);
        }
        return false;
    }

    private void InjectMessage(object data)
    {
        foreach (var (_, port) in _inputs)
        {
            if (port is ChannelInputPort<object> cp)
                cp.Write(data);
        }
    }

    // ── 持久化 ──

    public async Task PersistStateAsync()
    {
        var dir = Path.Combine(PathConfig.StoragePath, "Sessions", SessionId);
        Directory.CreateDirectory(dir);

        var meta = new
        {
            status = Status.ToString(),
            maxLevel = MaxLevel,
            createdAt = _createdAt,
            roundCount = _roundCount,
            componentIds = _componentIds
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "meta.json"),
            JsonConvert.SerializeObject(meta, Formatting.Indented));
    }

    public Task RestoreStateAsync()
    {
        var dir = Path.Combine(PathConfig.StoragePath, "Sessions", SessionId);
        var metaPath = Path.Combine(dir, "meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                var json = File.ReadAllText(metaPath);
                var meta = JsonConvert.DeserializeAnonymousType(json, new { status = "", maxLevel = 0, roundCount = 0 });
                if (meta != null)
                {
                    MaxLevel = meta.maxLevel;
                    _roundCount = meta.roundCount;
                    Status = meta.status switch
                    {
                        "Running" => SessionStatus.Restored,
                        "Crashed" => SessionStatus.Crashed,
                        _ => SessionStatus.Completed
                    };
                }
            }
            catch { /* 恢复失败不阻塞 */ }
        }
        return Task.CompletedTask;
    }
}
