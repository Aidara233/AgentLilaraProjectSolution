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
using AgentCoreProcessor.Logging;
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
    private string _componentLoopType = "channel";
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
        _componentLoopType = loopType == "session" ? "channel" : loopType;
        _componentHost = new ComponentHost(loopId, _componentLoopType, moduleBus, services, wakeLoop);
        _componentHost.InitAsync().GetAwaiter().GetResult();

        RefreshToolList();
    }

    /// <summary>
    /// session 工具表排除的独立工具：escalate（升级到工作模式）对已在工作模式的 session 无意义。
    /// 收尾由 end_session 负责（见 RunAsync 工具副作用），它对频道/任务 session 都适用。
    /// </summary>
    private static readonly HashSet<string> _sessionExcludedTools =
        new(StringComparer.OrdinalIgnoreCase) { "escalate" };

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
        // 合并 ToolRegistry 独立工具（排除 Express↔session 切换工具）
        foreach (var (name, tool) in ToolRegistry.All)
        {
            if (_sessionExcludedTools.Contains(name)) continue;
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

    // 运行时动态启用/卸载组件：尚未实现。
    // 当前 _componentHost 在 InitComponents 时一次性构建，运行期无法增删组件实例并重建工具表。
    // 真正实现需要：实例化新组件 → 注册其工具到 _componentHost → RefreshToolList。
    // 在落地前抛异常而非静默改 _componentIds，避免调用方误以为组件已生效。
    public void EnableComponent(string componentId)
        => throw new NotSupportedException("运行时动态启用组件尚未实现，组件须在 CreateSessionSpec.ComponentIds 中预先声明");

    public void DisableComponent(string componentId)
        => throw new NotSupportedException("运行时动态卸载组件尚未实现，组件须在 CreateSessionSpec.ComponentIds 中预先声明");

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

        // 生命周期信号：Continue 从触发者（escalate 所在的 Express 信号 / 监控循环）建立因果连线，
        // 并把 session 拉进自己的 scope（"session:{id}"）——否则继承的 Express 信号会很快 close，
        // session 下游所有事件都挂到已关闭的信号上，trace 页表现为红色孤儿节点。
        var parentCtx = SignalContext.Current;
        var lifeCtx = Signal.Continue(
            SignalContext.NewSignalId(), parentCtx?.CurrentSpanId,
            $"session:{SessionId}", LogGroup.Engine, "Session 生命周期",
            new { sessionId = SessionId, components = string.Join(",", _componentIds) });
        var endReason = "completed";

        // 等待 AgentHost 初始化（异步构建，最多等 3 秒）
        var hostWait = 0;
        while (_host == null && hostWait < 30)
        {
            await Task.Delay(100, ct);
            hostWait++;
        }

        // messages 是会话的常驻历史，跨突发累积，wait 挂起后再激活时保留——
        // 不再每突发清空（清空会丢失 wait 前的全部对话）。压缩/持久化留后续。
        List<Message> messages;
        if (_host != null)
        {
            try
            {
                messages = await _host.BuildStartAsync();
                Signal.Event(LogGroup.Engine, "BuildStartAsync 完成", new { sessionId = SessionId, messageCount = messages.Count });

                // 游标接力首轮播种：BuildStartAsync 取的历史快照最大 id 写回持久化游标。
                // 否则 session 若无新消息直接 end_session，ChannelEngine 恢复时会把这批历史当新消息重读。
                foreach (var (_, port) in _inputs)
                {
                    if (port is MessageInputPort mp && mp.ChannelId > 0)
                    {
                        var snapMax = _host.StateFor(mp.ChannelId).StartInjectMaxId;
                        AdvanceChannelCursor(mp.ChannelId, snapMax);
                    }
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "BuildStartAsync 失败，回退裸 InitialState", new { sessionId = SessionId, error = ex.Message });
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
            Signal.Warn(LogGroup.Engine, "AgentHost 未就绪，使用裸 InitialState", new { sessionId = SessionId });
        }

        int agentRound = 0;
        // 首轮已有 BuildStartAsync 的初始上下文，直接处理；之后每突发结束置 true 回到等待
        bool awaitingInput = messages.Count == 0;

        try
        {
            while (IsAlive && !ct.IsCancellationRequested)
            {
                // 等待新输入（突发间），或首轮无初始消息时
                if (awaitingInput)
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
                    awaitingInput = false;

                    // 新突发开始：重置突发内轮次计数（MaxAgentRounds 防单突发 runaway，非会话总轮次）
                    agentRound = 0;

                    // 轮次定价
                    _roundCount++;
                    ApplyRoundPricing();

                    // 收集输入，并通过 host 格式化（如有），追加到常驻历史 messages
                    var drained = DrainInputPortsRaw(ct);
                    if (drained.Count == 0)
                    {
                        awaitingInput = true;
                        continue;
                    }

                    if (_host != null)
                    {
                        try
                        {
                            // 只把有真实 channelId 的消息传给 BuildRoundAsync（host 只知道注册的频道）
                            var channelDrained = drained.Where(d => d.channelId > 0).ToList();
                            var systemDrained = drained.Where(d => d.channelId <= 0).ToList();

                            if (channelDrained.Count > 0)
                                messages.AddRange(await _host.BuildRoundAsync(channelDrained));

                            // 游标接力：每个频道推进到本轮消费的最大 message id
                            foreach (var grp in channelDrained.GroupBy(d => d.channelId))
                                AdvanceChannelCursor(grp.Key, grp.Max(d => d.msg.Id));

                            // 系统注入消息直接追加
                            foreach (var (_, um) in systemDrained)
                                messages.Add(new Message { Role = "user", Content = um.Content });

                            Signal.Event(LogGroup.Engine, "BuildRoundAsync 注入", new { sessionId = SessionId, added = drained.Count, historyCount = messages.Count });
                        }
                        catch (Exception ex)
                        {
                            Signal.Warn(LogGroup.Engine, "BuildRoundAsync 失败，回退裸内容", new { sessionId = SessionId, error = ex.Message });
                            messages.AddRange(drained.Select(d => new Message { Role = "user", Content = d.msg.Content }));
                        }
                    }
                    else
                    {
                        // fallback：裸内容
                        messages.AddRange(drained.Select(d => new Message { Role = "user", Content = d.msg.Content }));
                    }
                }

                // ── Agent 推理 ──
                if (_agentCore == null) break;

                _agentCore.EngineType = "channel"; // 复用 channel 工具白名单（所有 session 统一用 channel 过滤）
                _agentCore.CurrentModeId = null;   // 不分子模式，工具范围由组件决定（不过 ModeConfig 白名单）
                _agentCore.AdditionalTools = _tools.Values.ToList();

                agentRound++;
                if (agentRound > MaxAgentRounds)
                {
                    await SendOutput($"已达到最大轮次限制（{MaxAgentRounds}），会话即将结束。");
                    endReason = "max-rounds";
                    break;
                }

                // 每轮推理一条 span，承载模型调用 + 工具执行的耗时与结果
                using var roundSpan = Signal.Open(LogGroup.Engine, $"Session 轮次 #{agentRound}",
                    new { sessionId = SessionId, round = agentRound, toolCount = _tools.Count });

                ModelOutput output;
                try
                {
                    output = await _agentCore.InvokeAsync(messages, EngineMode.Working);
                }
                catch (Exception ex)
                {
                    Signal.Warn(LogGroup.Model, "Session 模型调用失败", new { sessionId = SessionId, round = agentRound, error = ex.Message });
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
                    roundSpan.SetCloseDetail(new
                    {
                        sessionId = SessionId,
                        round = agentRound,
                        tools = output.ToolCalls.Select((c, i) => new { tool = c.Tool, status = results[i].Status })
                    });

                    // assistant 轮：保留 tool_use ContentParts（携带 tool_use_id），与下方 tool_result 严格配对
                    // 替换上面那条空文本 assistant，避免模型看不到自己调用了工具（runaway 根因）
                    messages[messages.Count - 1] = NativeToolMessageFormatter.FormatAssistant(
                        output, _agentCore.UseNativeTools, resolver);

                    // tool_result 轮：一条消息按 tool_use_id 配对全部 call→result
                    messages.Add(NativeToolMessageFormatter.FormatToolResults(
                        output.ToolCalls, results, _agentCore.UseNativeTools, !_agentCore.DisableImageMessages));

                    // 副作用 + 停止判定（突发结束 ≠ 会话结束）
                    // endBurst：本次突发收尾，清消息回 WaitForActivationAsync，session 保活握 claim
                    // endSession：会话真正终结，break 走 finally 释放 claim
                    var endBurst = false;
                    var endSession = false;
                    for (int i = 0; i < output.ToolCalls.Count; i++)
                    {
                        var call = output.ToolCalls[i];
                        var result = results[i];

                        switch (call.Tool)
                        {
                            // speak/send_media 等 OutputOnly 工具自带发送，不在此重发（旧代码 SendOutput(result.Data) 会把消息ID当正文重发）
                            case "end_session":
                                // end_session 自身不发消息，这里把理由作为收尾发言发出；会话终结
                                await SendOutput(result.Data ?? "工作完成。");
                                endSession = true;
                                break;
                            case "wait":
                                // 挂起本突发，等下条消息或超时唤醒，session 保活
                                // 分钟数来自第二个参数 timeout_minutes（Inputs[1]），Inputs[0] 是 reason
                                var mins = 5;
                                if (call.Inputs.Count > 1 && int.TryParse(call.Inputs[1], out var parsed))
                                    mins = parsed;
                                SetWaitUntil(DateTime.UtcNow.AddMinutes(mins));
                                endBurst = true;
                                break;
                        }
                    }

                    // 内联 wait（speak 含 [wait] 占位符时 result.RequestWait=true）：挂起本突发
                    if (results.Any(r => r.RequestWait))
                        endBurst = true;

                    if (endSession)
                    {
                        Signal.Event(LogGroup.Engine, "endSession：退出循环释放 claim", new { sessionId = SessionId });
                        endReason = "end_session";
                        break;
                    }
                    if (endBurst)
                    {
                        Signal.Event(LogGroup.Engine, "endBurst：回等待保活",
                            new { sessionId = SessionId, historyCount = messages.Count, waitUntil = WaitUntil });
                        awaitingInput = true;
                        continue;
                    }
                }
                else
                {
                    // 无工具调用：模型给了最终回复，结束突发，等待下一轮输入（session 保活，历史保留）
                    Signal.Event(LogGroup.Engine, "无工具调用，结束突发回等待",
                        new { sessionId = SessionId, historyCount = messages.Count });
                    awaitingInput = true;
                }
            }
        }
        finally
        {
            if (Status == SessionStatus.Running)
                Status = SessionStatus.Completed;
            lifeCtx.Close(new { sessionId = SessionId, reason = endReason, status = Status.ToString() });
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
    /// 频道级游标接力：把本频道已处理到的最大 message id 写回持久化游标。
    /// session 占有期间推进同一份 ChannelContextPersistence 游标，使其始终是频道级唯一真相——
    /// session 结束后 ChannelEngine 从同一游标恢复，不重读 session 已处理的消息。
    /// </summary>
    private void AdvanceChannelCursor(int channelId, int maxMessageId)
    {
        if (channelId <= 0 || maxMessageId <= 0) return;
        try { new ChannelContextPersistence(channelId).AdvanceCursor(maxMessageId); }
        catch { /* best-effort，落地失败不中断会话 */ }
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
                if (data is UserMessage existing)
                {
                    // 入向接通投递的已入库消息：身份完整（SenderName/PlatformMessageId/Id），直接消费。
                    result.Add((existing.ChannelId, existing));
                }
                else if (data is EngineIncomingMessage em)
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
            // 排空后重置 MessageInputPort 队列计数器（新消息会在 Enqueue 时重新累加）
            if (port is MessageInputPort mp)
                mp.ResetQueueCount();
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
            // ① 非消息端口（系统注入/timeout/signal）有数据 → 立即激活
            foreach (var (name, port) in _inputs)
            {
                if (port is not MessageInputPort && port.TryPeek(out _))
                {
                    Signal.Debug(LogGroup.Engine, "WaitForActivation 唤醒：系统端口有输入", new { sessionId = SessionId, port = name });
                    return true;
                }
            }

            // ② @提及端口 (Weight >= 100) 有数据 → 立即激活
            foreach (var (name, port) in _inputs)
            {
                if (port is MessageInputPort mp && mp.Weight >= 100 && mp.TryPeek(out _))
                {
                    ResetAllMpQueueCounts();
                    Signal.Debug(LogGroup.Engine, "WaitForActivation 唤醒：@提及端口有输入",
                        new { sessionId = SessionId, port = name, weight = mp.Weight });
                    return true;
                }
            }

            // ③ 普通消息端口 (Weight < 100)：累积权重 >= 100 才激活（Weight=5 需 20 条）
            int totalWeight = 0;
            foreach (var (name, port) in _inputs)
            {
                if (port is MessageInputPort mp && mp.Weight < 100)
                    totalWeight += mp.QueueCount * mp.Weight;
            }
            if (totalWeight >= 100)
            {
                ResetAllMpQueueCounts();
                Signal.Debug(LogGroup.Engine, "WaitForActivation 唤醒：累积权重达标",
                    new { sessionId = SessionId, totalWeight });
                return true;
            }

            // ④ 检查 WaitUntil 是否到期
            if (WaitUntil != null && DateTime.UtcNow >= WaitUntil.Value)
            {
                Signal.Debug(LogGroup.Engine, "WaitForActivation 唤醒：WaitUntil 到期", new { sessionId = SessionId });
                InjectMessage(new { type = "timeout", wait_reason = "wait timeout expired" });
                return true;
            }

            await Task.Delay(200, ct);
        }
        Signal.Debug(LogGroup.Engine, "WaitForActivation 退出：ct 取消", new { sessionId = SessionId });
        return false;
    }

    private void ResetAllMpQueueCounts()
    {
        foreach (var (_, port) in _inputs)
        {
            if (port is MessageInputPort mp)
                mp.ResetQueueCount();
        }
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
                var meta = JsonConvert.DeserializeAnonymousType(json, new { status = "", roundCount = 0 });
                if (meta != null)
                {
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
