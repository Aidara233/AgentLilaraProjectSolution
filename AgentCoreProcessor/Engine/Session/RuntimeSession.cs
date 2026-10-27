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
using AgentCoreProcessor.Engine.Axes;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Util;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// 统一会话实现。行为由 CreateSessionSpec 驱动，不分子类。
/// </summary>
internal partial class RuntimeSession : IRuntimeSession, IRuntimeSessionDiagnostics
{
    private void RestoreAxesSnapshot(SessionStateSnapshot state)
    {
        // 轴值恢复：按轴名逐条校验，未知轴与非法值丢弃、缺失的补默认（见 AxisStore.Reset）。
        // ⚠️ 丢弃必须记日志——否则一次手改 state.json 就变成"某条轴静默回到默认值"。
        var droppedAxes = _axisStore.Reset(state.Axes);
        if (droppedAxes.Count > 0)
        {
            var names = new List<string>();
            foreach (var d in droppedAxes) names.Add($"{d.Axis}:{d.Reason}");
            Signal.Warn(LogGroup.Engine, "轴值恢复时丢弃了非法项", new { sessionId = SessionId, dropped = names });
        }

    }

    private void CaptureStepSnapshot(SnapshotWriter writer)
    {
        writer.Set(s => s.RoundCount, _roundCount);
    }

    private void CaptureContextSnapshot(SnapshotWriter writer)
    {
        writer.Set(s => s.CompressionTier, _compression?.CurrentTier.ToString() ?? "None");
    }

    private void CaptureAxesSnapshot(SnapshotWriter writer)
    {
        writer.Set(s => s.Axes, _axisStore.Snapshot());
    }

    public string SessionId { get; private set; } = Guid.NewGuid().ToString("N")[..12];
    public SessionStatus Status { get; private set; } = SessionStatus.Creating;
    public bool IsAlive => Status is SessionStatus.Running or SessionStatus.Creating or SessionStatus.Restored;
    public DateTime? WaitUntil { get; private set; }

    public IReadOnlyList<string> ComponentIds => _componentIds.AsReadOnly();
    public IReadOnlyList<string> ActiveTools => _activeTools.AsReadOnly();
    public IReadOnlyDictionary<string, IInputPort> Inputs => _inputs;
    public IReadOnlyDictionary<string, IOutputPort> Outputs => _outputs;
    public string? ParentSessionId { get; private set; }
    public string? EffectiveParentId { get; private set; }
    public IReadOnlyList<string> ChildSessionIds => _childSessionIds;
    public string Type { get; }
    public string? ForwardedTo { get; private set; }

    private readonly List<string> _componentIds = new();
    private readonly List<string> _childSessionIds = new();
    private readonly List<string> _activeTools = new();
    private readonly Dictionary<string, IInputPort> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IOutputPort> _outputs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 本会话的行为轴值（`axes` 模块的每会话一份）。
    /// <para>⚠️ 存储与端口分开：<see cref="AxisStore"/> 是**唯一写入点**，
    /// <see cref="AxisPort"/> 是只读查询面。别把写入口从端口漏出去——"谁能改轴"必须是一个能被枚举的问题。</para>
    /// </summary>
    private readonly AxisStore _axisStore = new();
    private IAxisPort? _axisPort;

    internal AxisStore AxisState => _axisStore;

    internal IAxisPort AxisPort => _axisPort ??= new AxisPort(_axisStore);

    /// <summary>
    /// 冻结本拍的轴快照。⚠️ **每拍调一次、拍内共用**：同一步内两次渲染必须逐字节相同，
    /// 否则每步都破提示词前缀。别在各消费者里各调一次。
    /// </summary>
    internal AxisSnapshot FreezeAxes() => AxisPort.Freeze(AxisState.Snapshot(), DateTimeOffset.UtcNow);

    /// <summary>
    /// 影子判定（只打日志、不改行为）：用 Graphs/preset.activation.json 复算"要不要激活"。
    /// 一致率 100% 后把 WaitForActivationAsync 的结论换成它。
    /// </summary>
    private readonly GraphActivationShadow? _activationShadow = GraphActivationShadow.TryCreate();
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 本会话显式 session-scope 挂载的私有图实例（一个会话可挂多张）。
    /// <para>入口和轮中排空后消费新消息；生产环境通过动态提供器刷新挂载。</para>
    /// </summary>
    private IReadOnlyList<KeywordGraphRunner> _graphRunners = Array.Empty<KeywordGraphRunner>();

    /// <summary>由 SessionEngine 在创建/恢复时绑定。</summary>
    internal void SetGraphRunners(IReadOnlyList<KeywordGraphRunner> runners) => _graphRunners = runners;

    private SessionContextHost? _host;

    /// <summary>
    /// 注入上下文宿主（由 <c>SessionEngine</c> 在创建/恢复时调一次）。
    /// ⚠️ 它住在 <c>Engine/Context</c>：这是**上下文宿主**，不是会话宿主（见类型注释）。
    /// </summary>
    public void SetContextHost(SessionContextHost host) => _host = host;

    private ComponentHost? _componentHost;
    private string _componentLoopType = "channel";
    private ICurrencyService? _currency;
    private IAgentModel? _agentCore;

    // ── 多核支持：白名单（来自会话类型配置）+ 活跃核 + 按核懒建缓存 ──
    private string _activeCoreConfig = "";
    private readonly List<string> _allowedCoreConfigs = new();
    private readonly Dictionary<string, IAgentModel> _coreInstances = new(StringComparer.OrdinalIgnoreCase);
    private ITool? _switchCoreTool;

    // ── 会话本体提示词（来自会话类型配置 PromptFiles，换核不变）──
    private string? _sessionPrompt;

    private MessageEngine? _messageEngine;

    /// <summary>图动作执行器（默认 null：图仍只观察，不发送）。</summary>
    private IGraphIntentDispatcher? _graphIntentDispatcher;
    private Func<string, Task>? _compact;
    private Func<string, Task>? _escalate;
    private IChannelConnectionAccess? _channelConnection;
    private ISessionExitQuery? _exitQuery;
    private Func<IWriterAccess?>? _writerAccess;
    private IServiceProvider? _services;
    private readonly ConnectedChannelState _connected = new();
    /// <summary>当前连接频道的输入端口（normal + 可选 mention），断开时退订。</summary>
    private readonly List<IInputPort> _connectedPorts = new();
    private readonly List<ToolCall> _initActions;
    private object? _initialState;
    private int _roundCount;
    private DateTime _createdAt = DateTime.UtcNow;
    private DateTime _lastActivity = DateTime.UtcNow;
    private CancellationTokenSource? _cts;
    /// <summary>外部唤醒信号（异步任务完成、跨循环通知等）。</summary>
    private volatile bool _wakeSignaled;
    /// <summary>是否正处在一个突发（burst）的处理中（agent 推理进行中）。供 --test / WebUI 判断会话忙碌。</summary>
    private volatile bool _isBusy;
    public bool IsBusy => _isBusy;

    /// <summary>
    /// 本突发当前处于哪一段。**只读、只供诊断**（日志 / 控制面 / 排查），不参与任何判定。
    ///
    /// <para>⚠️ 它是 `step` 的 `phase` 字段的第一版实现：按 12 阶段表的边界赋值，
    /// 但**没有把 420 行的 <c>RunAsync</c> 拆成方法**——那是 B 阶段切文件时的事。
    /// 先有可观测的 phase，才好在拆分前后对照"行为没变"。</para>
    ///
    /// <para>⚠️ 12 阶段表见 <c>docs/modularity/module-io-spec.md</c> §5.3；
    /// 这里只落到 5 个粗档，因为粗档已经能回答"卡在哪一段"，
    /// 再细就要和方法边界一起定（分阶段拆方法在 B 阶段）。</para>
    /// </summary>
    internal StepPhase Phase { get; private set; } = StepPhase.Idle;

    /// <summary>end_session 强制退出计数：0=未触发，>=1=已拦截过一次。</summary>
    private int _forceExitAttempts;

    // ── 会话历史（字段化，跨突发累积，wait 挂起后再激活时保留）──
    private List<Message> _history = new();
    public List<Message> History => _history;

    // ── 持久化器（每轮写入 history.jsonl + state.json）──
    private SessionFileStore? _persister;
    public void SetPersister(SessionFileStore p) => _persister = p;

    // ── 恢复标记：Restored 的 session 跳过 BuildStartAsync / bootstrap 注入，直接等新消息 ──
    private bool _isRestored;

    // ── 压缩（L1/L2 提示注入活跃，L3 锁定留 Phase 5）──
    private CompressionTierModule? _compression;
    internal ITool? _compactTool;  // Phase 5 注入具体 CompactTool 实例
    private bool _l3Lockdown;
    private Dictionary<string, ITool>? _preLockdownTools;

    /// <summary>会话完成时触发（正常结束/异常/崩溃）。</summary>
    public event Action<IRuntimeSession>? OnCompleted;

    private const int FreeRounds = 20;
    /// <summary>express 轻量会话 L1 首次压缩提醒阈值（Flash 模型窗口较 Working 小，调低提前量；L2/L3 沿用默认）。</summary>
    private const int ExpressL1Tokens = 40000;

    public RuntimeSession(CreateSessionSpec spec, string[]? finalComponents = null)
    {
        _spec = spec;
        Type = spec.Type;
        ParentSessionId = spec.ParentSessionId;
        if (finalComponents != null)
            _componentIds.AddRange(finalComponents);
        else
            throw new ArgumentException("finalComponents 由 SessionEngine 计算，不应为 null", nameof(finalComponents));
        _initialState = spec.InitialState;
        _initActions = spec.InitActions ?? new List<ToolCall>();
    }

    /// <summary>恢复专用：复用已有 SessionId，避免重启后 ID 变动。finalComponents 必填——
    /// 组件装配只能由 SessionEngine 计算（CreateSession/RestoreAll 共用 ComputeFinalComponents），
    /// 裸 new RuntimeSession(spec, id) 会因组件清单为空而抛异常（486b5841 起构造函数拒绝 null）。</summary>
    internal RuntimeSession(CreateSessionSpec spec, string fixedSessionId, string[] finalComponents)
        : this(spec, finalComponents)
    {
        SessionId = fixedSessionId;
    }

    // ── 依赖注入 ──

    public void SetCurrency(ICurrencyService currency) => _currency = currency;


    public void SetMessageEngine(MessageEngine me) => _messageEngine = me;
    public void SetGraphIntentDispatcher(IGraphIntentDispatcher dispatcher) => _graphIntentDispatcher = dispatcher;
    public void SetTransitionOperations(Func<string, Task> compact, Func<string, Task> escalate)
    {
        _compact = compact;
        _escalate = escalate;
    }
    public void SetChannelConnectionAccess(IChannelConnectionAccess access) => _channelConnection = access;
    public void SetSessionExitQuery(ISessionExitQuery query) => _exitQuery = query;
    public void SetWriterAccess(Func<IWriterAccess?> writerAccess) => _writerAccess = writerAccess;

    /// <summary>当前连接频道状态（注入组件 perLoopServices，供发送工具读取）。</summary>
    public ConnectedChannelState ConnectedChannel => _connected;


    /// <summary>
    /// 初始化压缩模块：CompressionTierModule（Evaluate + GetInjectText） + CompactTool（注入 _tools）。
    /// AgentConfig 阈值：L1=80k / L2=130k / L3=150k，匹配 200k 上下文模型。
    /// L3 触发后：_l3Lockdown=true，下一轮 RuntimeSession 会清空 _tools 只留 compact，模型必调。
    /// </summary>
    internal void InitCompression(AgentConfig? config = null)
    {
        var cfg = config ?? new AgentConfig();
        _compression = new CompressionTierModule(
            cfg,
            getHistory: () => _history,
            onL3Triggered: () => _l3Lockdown = true,
            // express 轻量会话：L1 首次提醒阈值调低（40k），L2/L3 保持默认
            l1Tokens: Type == "express" ? ExpressL1Tokens : null
        );

        // 创建 CompactTool 并注入工具表
        if (_compact != null)
        {
            _compactTool = new Tool.Core.CompactTool(SessionId, _compact);
            if (!_tools.ContainsKey("compact"))
            {
                _tools["compact"] = _compactTool;
                _activeTools.Add("compact");
            }
        }
    }

    /// <summary>恢复存档后灌入运行状态 + 历史。</summary>
    internal void RestoreFromSnapshot(SessionStateSnapshot state, List<Message> history)
    {
        foreach (var port in SnapshotPorts()) port.Restore(state);
        _history = history ?? new List<Message>();
    }

    /// <summary>compact 后新 session 接续旧 session 的轮次计数。</summary>
    internal void InheritRoundCount(int parentRoundCount)
    {
        _roundCount = parentRoundCount;
    }

    // ── compact 工具读取旧 session 信息的访问器 ──

    /// <summary>compact 创建新 session 时复用旧 session 的端口配置。</summary>
    internal Dictionary<string, PortSpec> BuildPortSpecSnapshotForCompact()
    {
        var ports = new Dictionary<string, PortSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, port) in _inputs)
        {
            if (port is MessageInputPort mp)
            {
                ports[name] = new PortSpec
                {
                    ChannelId = mp.ChannelId,
                    SubStream = mp.SubStream ?? "normal",
                    Weight = mp.Weight,
                    PreloadHistory = false  // compact 新 session 上下文来自摘要，不重读历史
                };
            }
        }
        return ports;
    }

    /// <summary>compact 重建 InitActions：仅 connect_channel 重新连接原频道（claim 已释放需重建）。</summary>
    internal List<ToolCall> GetInitActionsForCompact()
    {
        if (_connected.CurrentChannelId > 0)
        {
            return new List<ToolCall>
            {
                new() { Tool = "connect_channel", Inputs = new List<string> { _connected.CurrentChannelId.ToString() } }
            };
        }
        return new List<ToolCall>();
    }

    /// <summary>compact 新 session 接续旧 session 的 _roundCount。</summary>
    internal int GetRoundCountForCompact() => _roundCount;

    // ── 端口管理 ──

    public void AddInputPort(string name, IInputPort port) => _inputs[name] = port;
    public void AddOutputPort(string name, IOutputPort port) => _outputs[name] = port;

    // ── 频道连接 ──
    // 连接 = 统一动作，同时管输入（订阅+历史）、输出（speak 去向）、占用（claim→停 express）。
    // 独占单频道：连新的先断旧的。connect_channel/listen_channel/disconnect_channel 工具与
    // InitActions bootstrap 都通过 ApplyToolEffect 走到这里，逻辑单一来源。

    /// <summary>连接动作产生的上下文消息（频道信息+参与者+历史 / 系统提示），由 RunAsync 注入下一轮。</summary>
    private readonly List<Message> _pendingInjections = new();

    /// <summary>bootstrap（InitActions）生成的初始化系统消息，CreateSession 同步生成，RunAsync 首轮消费。</summary>
    private readonly List<Message> _bootstrapMessages = new();

    /// <summary>创建规格（含 SessionServices per-session 服务）。InitComponents 注入用。</summary>
    private readonly CreateSessionSpec _spec;


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
        // 不再每突发清空（清空会丢失 wait 前的全部对话）。提升为 _history 字段后持久化钩子可触及每轮新增消息。
        //
        // 恢复的 session（_isRestored=true）跳过 BuildStartAsync / bootstrap / pendingInjections——
        // _history 已从存档灌入完整对话历史，频道信息+参与者+InitialState 在旧 session 创建时已注入，
        // 重复注入会导致模型收到冗余上下文并立即激活一次推理（非期望行为）。
        // 恢复后直接 awaitingInput=true，等新消息到达再激活。
        if (!_isRestored && _host != null)
        {
            try
            {
                var startMsgs = await _host.BuildStartAsync();
                _history.AddRange(startMsgs);
                Signal.Event(LogGroup.Engine, "BuildStartAsync 完成", new { sessionId = SessionId, messageCount = _history.Count });

                // 游标接力首轮播种：BuildStartAsync 取的历史快照最大 id 写回持久化游标。
                // 否则 session 若无新消息直接 end_session，ExpressEngine 恢复时会把这批历史当新消息重读。
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
                if (_initialState is string initText && !string.IsNullOrEmpty(initText))
                    _history.Add(new Message { Role = "user", Content = initText });
            }

            // 会话开头一次性静态参考（技能清单等）：仅新会话首轮注入一次，随首轮持久化落盘，
            // 恢复会话天然跳过（清单已在存档历史中）。与后续每突发注入的 [组件通知] 互不混淆。
            AppendStartReferenceSections();

            // 持久化首轮 Bootstrap（存档落到 history.jsonl + state.json）
            await PersistRoundAsync(_history.ToList());
        }
        else if (!_isRestored)
        {
            // AgentHost 未就绪，fallback 到裸 InitialState
            if (_initialState is string initText && !string.IsNullOrEmpty(initText))
                _history.Add(new Message { Role = "user", Content = initText });
            Signal.Warn(LogGroup.Engine, "AgentHost 未就绪，使用裸 InitialState", new { sessionId = SessionId });
        }

        // 初始化工具调用（bootstrap）：claim/订阅/连接已在 CreateSession 同步完成（路由前提），
        // 这里仅消费已生成的初始化系统消息 + 连接上下文，注入首轮。
        // 恢复的 session 跳过（InitActions 已在旧 session 执行过，不重复 claim/订阅）
        if (!_isRestored && _bootstrapMessages.Count > 0)
        {
            _history.AddRange(_bootstrapMessages);
            _bootstrapMessages.Clear();
            await PersistRoundAsync(_history.ToList());  // 追加 bootstrap 消息到存档
        }

        // 连接动作产生的频道上下文（历史等）追加进首轮
        // 恢复的 session 跳过（频道上下文已在 _history 中）
        if (!_isRestored && _pendingInjections.Count > 0)
        {
            var pendingCopy = _pendingInjections.ToList();
            _history.AddRange(pendingCopy);
            _pendingInjections.Clear();
            await PersistRoundAsync(pendingCopy);
        }

        int agentRound = 0;
        // 首轮已有 BuildStartAsync 的初始上下文，直接处理；之后每突发结束置 true 回到等待
        // 恢复的 session：_history 已有完整历史，但不立即激活——等新消息到达再唤醒
        // express 会话：强制等待输入（awaitingInput=true），按端口权重激活——被动响应，不因 InitialState
        //   立即推理（否则第一条普通消息就触发响应，绕过权重节流）。背景上下文（参与者/历史/InitialState）
        //   在 _history 中，激活后由 BuildRoundAsync 追加新消息。
        bool awaitingInput = _isRestored || _history.Count == 0 || Type == "express";

        try
        {
            while (IsAlive && !ct.IsCancellationRequested)
            {
                // 等待新输入（突发间），或首轮无初始消息时
                bool hasComponentNotification = false;
                if (awaitingInput)
                {
                    if (!await WaitForActivationAsync(_cts.Token))
                        break;

                    TouchActivity();
                    SetWaitUntil(null);
                    awaitingInput = false;
                    _isBusy = true; // 突发开始（agent 推理进行中）
                    Phase = StepPhase.Draining;

                    // 新突发开始：重置突发内轮次计数
                    agentRound = 0;

                    // 轮次定价
                    _roundCount++;
                    ApplyRoundPricing();

                    // ── 组件通知排空：异步任务完成通知（下载/SSH 等），在早返回之前先排空 ──
                    if (_componentHost != null)
                    {
                        await _componentHost.OnBeforeInvokeAsync();
                        var sections = _componentHost.BuildPromptSections();
                        AppendGlobalPromptSections(sections);
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

                    // 收集输入，并通过 host 格式化（如有），追加到常驻历史 messages
                    var drained = DrainInputPortsRaw(ct);
                    await ConsumeSessionGraphsAsync(drained);
                    if (drained.Count == 0 && _pendingInjections.Count == 0 && !hasComponentNotification)
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

                            List<Message> newRoundMsgs = new();
                            if (channelDrained.Count > 0)
                            {
                                var built = await _host.BuildRoundAsync(channelDrained);
                                newRoundMsgs.AddRange(built);
                                _history.AddRange(built);
                            }

                            // 游标接力：每个频道推进到本轮消费的最大 message id
                            foreach (var grp in channelDrained.GroupBy(d => d.channelId))
                                AdvanceChannelCursor(grp.Key, grp.Max(d => d.msg.Id));

                            // 系统注入消息直接追加
                            foreach (var (_, um, _) in systemDrained)
                            {
                                var m = new Message { Role = "user", Content = um.Content };
                                newRoundMsgs.Add(m);
                                _history.Add(m);
                            }

                            Signal.Event(LogGroup.Engine, "BuildRoundAsync 注入", new { sessionId = SessionId, added = drained.Count, historyCount = _history.Count });
                            await PersistRoundAsync(newRoundMsgs);
                        }
                        catch (Exception ex)
                        {
                            Signal.Warn(LogGroup.Engine, "BuildRoundAsync 失败，回退裸内容", new { sessionId = SessionId, error = ex.Message });
                            var fallback = drained.Select(d => new Message { Role = "user", Content = (d.isMention ? "⚠️ [被@提及]\n" : "") + d.msg.Content }).ToList();
                            _history.AddRange(fallback);
                            await PersistRoundAsync(fallback);
                        }
                    }
                    else
                    {
                        // fallback：裸内容
                        var fallback = drained.Select(d => new Message { Role = "user", Content = (d.isMention ? "⚠️ [被@提及]\n" : "") + d.msg.Content }).ToList();
                        _history.AddRange(fallback);
                        await PersistRoundAsync(fallback);
                    }
                }

                // ── Agent 推理 ──
                if (_agentCore == null) break;

                // 推理前准备（B-4e：从主循环抽出）
                var roundContext = await PrepareInferenceAsync(hasComponentNotification);
                var roundCore = roundContext.Core;
                var invokeRequest = roundContext.Request;

                agentRound++;

                // 中途注入（B-4g：从主循环抽出）
                await DrainMidRoundAsync();

                // 每轮推理一条 span，承载模型调用 + 工具执行的耗时与结果
                // ⚠️ span 的 using 覆盖"模型调用 + 工具执行"两段，所以它留在主循环：
                // 工具段的 SetCloseDetail 在 ExecuteToolsAsync 里，把 span 收进调用方法会提前释放。
                using var roundSpan = Signal.Open(LogGroup.Engine, $"Session 轮次 #{agentRound}",
                    new { sessionId = SessionId, round = agentRound, toolCount = _tools.Count });

                Phase = StepPhase.Inferring;
                var maybeOutput = await InvokeModelAsync(roundCore, invokeRequest, agentRound);
                if (maybeOutput is null)
                    continue;   // 模型调用失败：已记 [错误]，同一突发内再试一轮

                // ⚠️ ModelOutput 是 readonly struct ⇒ ModelOutput? 是 Nullable<ModelOutput>，取 .Value（上面的 is null 已排除空）。
                ModelOutput output = maybeOutput.Value;
                _history.Add(new Message { Role = "assistant", Content = output.Text ?? output.Thinking ?? "" });

                // ── 工具执行 ──
                Phase = StepPhase.ExecutingTools;
                if (output.HasToolCalls && output.ToolCalls != null && output.ToolCalls.Count > 0)
                {
                    var results = await ExecuteToolsAsync(output, output.ToolCalls!, roundCore, roundSpan, agentRound);

                    // 副作用 + 停止判定（突发结束 ≠ 会话结束）
                    Phase = StepPhase.Judging;

                    // 工具结果解释（B-4b：从主循环抽出）
                    var outcome = await InterpretToolEffectsAsync(output.ToolCalls!, results);

                    // 判定（B-4b：从主循环抽出）
                    var action = DecideStepAction(outcome, out var judgeReason);
                    if (action == StepAction.Stop)
                    {
                        endReason = judgeReason;
                        break;
                    }
                    if (action == StepAction.Restart)
                    {
                        awaitingInput = true;
                        Phase = StepPhase.Waiting;
                        continue;
                    }
                }
                else
                {
                    // 模型无工具调用：注入提醒继续循环（Working session 不应自动挂起）
                    await InjectContinueReminderAsync(agentRound);
                }
            }
        }
        finally
        {
            _isBusy = false;
            if (Status == SessionStatus.Running)
                Status = SessionStatus.Completed;
            lifeCtx.Close(new { sessionId = SessionId, reason = endReason, status = Status.ToString() });

            // 会话结束：phase 回到 Idle（否则诊断面板会把"已结束"读成"卡在某段"）。
            Phase = StepPhase.Idle;

            // 完成事件由 SessionEngine 释放本会话绑定的 session-scope 图实例。
            OnCompleted?.Invoke(this);
        }
    }

    /// <summary>
    /// 模型本轮没有调用任何工具时，注入一条「继续工作」的提醒并落盘（B-4a，从 <see cref="RunAsync"/> 抽出）。
    ///
    /// <para>⚠️ 这是 Working 会话**不自动挂起**的机制：没有工具调用不等于任务结束，
    /// 会话必须靠模型自己给出 <c>end_session</c>（终结）或 <c>wait</c>（挂起）才收尾。</para>
    ///
    /// <para>⚠️ 落盘写的是 `_history` 的**最后两条**（assistant + 刚注入的 reminder）：
    /// 依赖"调用前 assistant 消息已入历史"这一前置。抽方法时保留了这条隐式依赖，
    /// 所以本方法不能挪到 assistant 入历史之前。</para>
    /// </summary>
    private async Task InjectContinueReminderAsync(int agentRound)
    {
        Signal.Event(LogGroup.Engine, "无工具调用，注入提醒继续",
            new { sessionId = SessionId, round = agentRound });

        var reminder = new Message
        {
            Role = "user",
            Content = "[系统] 你没有调用任何工具。如果任务完成请用 end_session 结束，需要等人回复用 wait 挂起，否则继续工作。"
        };
        _history.Add(reminder);

        // assistant + reminder 一起写
        await PersistRoundAsync(new List<Message>
        {
            _history[_history.Count - 2],
            _history[_history.Count - 1]
        });
    }

    private void InjectMessage(object data)
    {
        var text = data is string s ? s : Newtonsoft.Json.JsonConvert.SerializeObject(data);
        _pendingInjections.Add(new Message { Role = "user", Content = $"[系统] {text}" });
    }

    /// <summary>
    /// 解释一批工具结果对控制流的影响（B-4b，从 <see cref="RunAsync"/> 抽出）。
    ///
    /// <para>逐个调用 <see cref="ToolEffectInterpreter"/> 的接线 <see cref="ApplyToolEffectAsync"/>，
    /// 把各自的 <c>EndBurst</c> / <c>EndSession</c> 汇总；另外把 <c>RequestWait</c>
    /// （<c>speak</c> 含 <c>[wait]</c> 占位符时的内联挂起）也算作收尾本突发。</para>
    ///
    /// <para>⚠️ **不能短路**：即使某条工具已经给出 <c>EndSession</c>，后面每条仍要解释并执行其动作
    /// （例如 <c>end_session</c> 与 <c>connect_channel</c> 同时出现时，连接的副作用仍应发生）。
    /// 原实现就是全量遍历，这里逐字保留。</para>
    /// </summary>
    private async Task<ToolBatchOutcome> InterpretToolEffectsAsync(List<ToolCall> calls, IReadOnlyList<ToolResult> results)
    {
        var outcome = ToolBatchOutcome.None;

        for (var i = 0; i < calls.Count; i++)
        {
            var effect = await ApplyToolEffectAsync(calls[i], results[i]);
            outcome = outcome.Merge(new ToolBatchOutcome(effect.EndBurst, effect.EndSession));
        }

        // 内联 wait（speak 含 [wait] 占位符时 result.RequestWait=true）：挂起本突发
        if (results.Any(r => r.RequestWait))
            outcome = outcome.WithEndBurst(true);

        return outcome;
    }

    /// <summary>
    /// 由工具判定结果决定主循环去向（B-4b，从 <see cref="RunAsync"/> 抽出）。
    ///
    /// <para>⚠️ **只做判定，不做状态切换**：<c>awaitingInput</c> 是主循环的局部变量，
    /// 本方法够不到它——所以返回 <see cref="StepAction.Restart"/>，由调用方置位。
    /// 这是刻意的：一旦把 <c>awaitingInput</c> 提成字段，express 会话的"被动等待"语义
    /// 就会散在两个地方，谁都能改。</para>
    ///
    /// <para>⚠️ <c>_forceExitAttempts</c> 的清零**只在回等待时做**（不终结的情形）：
    /// 终结路径不需要清零，因为会话要结束了。</para>
    /// </summary>
    private StepAction DecideStepAction(ToolBatchOutcome outcome, out string endReason)
    {
        endReason = "completed";

        if (outcome.EndSession)
        {
            _isBusy = false;
            Signal.Event(LogGroup.Engine, "endSession：退出循环释放 claim", new { sessionId = SessionId });
            endReason = "end_session";
            return StepAction.Stop;
        }

        if (outcome.EndBurst)
        {
            _isBusy = false;
            Signal.Event(LogGroup.Engine, "endBurst：回等待保活",
                new { sessionId = SessionId, historyCount = _history.Count, waitUntil = WaitUntil });
            _forceExitAttempts = 0;
            return StepAction.Restart;
        }

        return StepAction.Proceed;
    }
    /// <summary>
    /// 执行本轮模型调用产出的全部工具，并把这一轮（assistant tool_use + tool_result）配好对、落盘（B-4c，从 <see cref="RunAsync"/> 抽出）。
    ///
    /// <para>⚠️ <b>顺序是这个方法的核心，不能调换</b>：</para>
    /// <list type="number">
    ///   <item>执行 → <item>把 span 的收尾详情写好（必须紧贴执行之后，否则耗时会算到别处）
    ///     → <item>用 native 格式**重写**最后那条 assistant 消息（携带 <c>tool_use_id</c>）
    ///     → <item>按 <c>tool_use_id</c> 配对写入 tool_result
    ///     → <item>两条一起落盘。</item></list>
    /// </list>
    ///
    /// <para>⚠️ 第 3 步的"重写"不是可有可无：原实现先写了一条空文本 assistant，再替换它。
    /// 不替换的话模型看不到自己调用了工具——这是历史上的 runaway 根因。</para>
    ///
    /// <para>⚠️ <paramref name="roundSpan"/> 由调用方持有：它的 <c>using</c> 生命周期覆盖
    /// "模型调用 + 工具执行"两段，所以必须传进来而不是在这里开（B-4d 会把这段收进模型调用方法）。</para>
    /// </summary>
    private async Task<IReadOnlyList<ToolResult>> ExecuteToolsAsync(
        ModelOutput output, List<ToolCall> calls, IAgentModel roundCore, SpanHandle roundSpan, int agentRound)
    {
        Func<string, ITool?> resolver = name =>
            _tools.TryGetValue(name, out var t) ? t :
              _componentHost?.TryGetTool(name) ?? ToolRegistry.Get(name);

        var executor = ToolExecutorFactory.Create(resolver);
        executor.PermissionGroup = _componentLoopType;
        executor.SessionId = SessionId;
        executor.ChannelId = _connected.CurrentChannelId;

        var results = await executor.ExecuteAsync(calls);

        roundSpan.SetCloseDetail(new
        {
            sessionId = SessionId,
            round = agentRound,
            tools = calls.Select((c, i) => new { tool = c.Tool, status = results[i].Status })
        });

        // assistant 轮：保留 tool_use ContentParts（携带 tool_use_id），与下方 tool_result 严格配对
        // 替换上面那条空文本 assistant，避免模型看不到自己调用了工具（runaway 根因）
        _history[_history.Count - 1] = NativeToolMessageFormatter.FormatAssistant(
            output, roundCore.UseNativeTools, resolver);

        // tool_result 轮：一条消息按 tool_use_id 配对全部 call→result
        var resultMsg = NativeToolMessageFormatter.FormatToolResults(
            calls, results, roundCore.UseNativeTools, !roundCore.DisableImageMessages);
        _history.Add(resultMsg);

        // 每轮持久化：assistant turn + tool_result turn 一起写
        await PersistRoundAsync(new List<Message>
        {
            _history[_history.Count - 2],
            _history[_history.Count - 1]
        });

        return results;
    }
    internal string[] GetComponentIdsForCompact() => _componentIds.ToArray();

    /// <summary>
    /// 调一次模型（B-4d，从 <see cref="RunAsync"/> 抽出）。失败返回 <see langword="null"/>，由调用方决定去留。
    ///
    /// <para>⚠️ <b>三件事的顺序不能改</b>：</para>
    /// <list type="number">
    ///   <item>开线上请求体捕获（必须在调用**之前**开作用域，否则抓不到请求）</item>
    ///   <item>调 <c>InvokeAsync</c></item>
    ///   <item>影子快照（必须**紧贴**调用之后：下一次 <c>InvokeAsync</c> 会重设客户端历史）</item>
    /// </list>
    ///
    /// <para>⚠️ **失败不抛，只记 <c>[错误]</c> 并返回 null**：原实现是 <c>catch</c> + <c>continue</c>，
    /// 让同一突发内再跑一轮。把异常抛给调用方会改变这条语义（会话会直接结束）。</para>
    ///
    /// <para>⚠️ 影子快照默认 Off 时是空操作，Legacy 路径一个字节都不变——不要"顺手"让它变成必做项。</para>
    /// </summary>
    private async Task<ModelOutput?> InvokeModelAsync(IAgentModel roundCore, AgentInvokeRequest invokeRequest, int agentRound)
    {
        try
        {
            return await roundCore.InvokeAsync(_history, invokeRequest);
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Model, "Session 模型调用失败",
                new { sessionId = SessionId, round = agentRound, error = ex.Message });
            _history.Add(new Message { Role = "system", Content = $"[错误] 模型调用失败: {ex.Message}" });
            return null;
        }
    }
    /// <summary>
    /// 每轮推理前的**中途注入**（B-4g，从 <see cref="RunAsync"/> 抽出）：检查端口有没有新消息，有就立刻并进上下文。
    ///
    /// <para>⚠️ 它与"入口排空"是**两个不同时序的调用点**（H3）：入口那一次发生在突发开始、等激活之后；
    /// 这一次发生在**每一轮推理之前**且**不阻塞**（<c>CancellationToken.None</c>）。
    /// 两者不能合并成一个方法——合并会丢掉"中途插入的话能不能被本轮看到"这个语义。</para>
    ///
    /// <para>⚠️ 游标推进（<c>AdvanceChannelCursor</c>）在这里也做一次：中途注入的消息同样算"已消费"，
    /// 否则下一轮会重复注入同一条。</para>
    /// </summary>
    private async Task DrainMidRoundAsync()
    {
                {
                    var midDrain = DrainInputPortsRaw(CancellationToken.None);
                    await ConsumeSessionGraphsAsync(midDrain);
                    if (midDrain.Count > 0)
                    {
                        List<Message> midNew = new();
                        if (_host != null)
                        {
                            var cd = midDrain.Where(d => d.channelId > 0).ToList();
                            var sd = midDrain.Where(d => d.channelId <= 0).ToList();
                            if (cd.Count > 0)
                            {
                                var built = await _host.BuildRoundAsync(cd);
                                midNew.AddRange(built);
                                _history.AddRange(built);
                            }
                            foreach (var grp in cd.GroupBy(d => d.channelId))
                                AdvanceChannelCursor(grp.Key, grp.Max(d => d.msg.Id));
                            foreach (var (_, um, _) in sd)
                            {
                                var m = new Message { Role = "user", Content = um.Content };
                                midNew.Add(m);
                                _history.Add(m);
                            }
                        }
                        else
                        {
                            var fallback = midDrain.Select(d => new Message { Role = "user", Content = (d.isMention ? "⚠️ [被@提及]\n" : "") + d.msg.Content }).ToList();
                            midNew.AddRange(fallback);
                            _history.AddRange(fallback);
                        }
                        if (midNew.Count > 0)
                            await PersistRoundAsync(midNew);
                    }
        }
    }

    /// <summary>
    /// 推理前准备（B-4e，从 <see cref="RunAsync"/> 抽出）：把四类"本轮该看到什么"的东西依次塞进历史，
    /// 然后产出本轮的核引用与调用参数。
    ///
    /// <para>⚠️ <b>顺序是契约</b>（与原实现逐字一致）：</para>
    /// <list type="number">
    ///   <item>组件通知补做（仅当突发入口没做过）——<paramref name="hasComponentNotification"/> 就是那个信号</item>
    ///   <item>`_pendingInjections` 刷入（上一轮 connect_channel 产生的频道上下文）</item>
    ///   <item>压缩提示 L1/L2 注入（按估算 token 取档）</item>
    ///   <item>L3 锁定：**只发一条 mutation**，不直接改工具表（H1 的修法）</item>
    ///   <item>构建 `invokeRequest` 并锁定 `roundCore`</item>
    /// </list>
    ///
    /// <para>⚠️ 第 5 步的两者**必须成对**：`roundCore` 是"本轮锁定的核"（外置切换可能在本轮中途发生），
    /// 而 `invokeRequest` 带着同一刻的工具表。分开取会拿到两个不同时刻的快照。</para>
    ///
    /// <para>⚠️ 本方法**不碰 `awaitingInput`**，也不做任何跳转——所以它是 B-4f 之外唯一安全的抽取。</para>
    /// </summary>
    private async Task<RoundContext> PrepareInferenceAsync(bool hasComponentNotification)
    {
        // 组件通知排空：异步任务完成通知（下载/SSH/群文件等）→ prompt section
        if (!hasComponentNotification && _componentHost != null)
        {
            await _componentHost.OnBeforeInvokeAsync();
            var sections = _componentHost.BuildPromptSections();
            AppendGlobalPromptSections(sections);
            if (sections.Count > 0)
            {
                var notifyMsg = new Message { Role = "user", Content = "[组件通知]\n" + string.Join("\n\n", sections) };
                _history.Add(notifyMsg);
                Signal.Event(LogGroup.Engine, "组件通知注入",
                    new { sessionId = SessionId, sectionCount = sections.Count });
            }
        }

        // 连接动作（上一轮 connect_channel）产生的频道上下文，注入本轮推理前
        if (_pendingInjections.Count > 0)
        {
            var pendingCopy = _pendingInjections.ToList();
            _history.AddRange(pendingCopy);
            _pendingInjections.Clear();
            await PersistRoundAsync(pendingCopy);
        }

        // ── 压缩提示注入（L1/L2 文字，L3 锁定待 Phase 5 CompactTool 注入后启用）──
        if (_compression != null)
        {
            var estimatedTokens = _history.Sum(m => EstimateChars(m)) / 3;
            var compressHint = _compression.GetInjectText(estimatedTokens);
            if (compressHint != null)
            {
                var hintMsg = new Message { Role = "user", Content = compressHint };
                _history.Add(hintMsg);
                _compression.MarkL1Injected();
                Signal.Event(LogGroup.Engine, "压缩提示注入",
                    new { sessionId = SessionId, tier = _compression.CurrentTier.ToString(), estimatedTokens });
            }

            // L3 锁定：仅当 _compactTool 已注入（Phase 5）才走锁定路径
            if (_l3Lockdown && _compactTool != null && _preLockdownTools == null)
            {
                _l3Lockdown = false;
                // ⚠️ 上下文层**不再直接改工具表**：只产出一条变更意图，由会话统一应用。
                // 这是 H1 的修法——原来的 _tools.Clear() 是一条从 context 到工具层的反向数据流。
                ApplyContextMutation(new LockToolTableTo("compact"));
            }
        }

        // ⚠️ H2：不再把本轮参数写进 _agentCore 的字段——改为随调用传入
        // （AgentInvokeRequest），参数的生命周期因此只覆盖这一次模型调用。
        var invokeRequest = new AgentInvokeRequest(
            EngineType: _componentLoopType,          // 权限组（channel/task）
            AdditionalTools: _tools.Values.ToList());

        // 硬化：本轮锁定的核引用。外置切换（命令/WebUI）可能在本轮中途发生，
        // 整轮用 roundCore 保证格式化标志（UseNativeTools 等）与产出本轮的核一致。
        var roundCore = _agentCore!;

        return new RoundContext(invokeRequest, roundCore);
    }
}
