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
internal class RuntimeSession : IRuntimeSession
{
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
    /// <para>⚠️ 目前只做绑定与释放，尚未在会话轮次里消费——真正的消费点落地前这里不会产生行为。</para>
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
    private AgentCore? _agentCore;

    // ── 多核支持：白名单（来自会话类型配置）+ 活跃核 + 按核懒建缓存 ──
    private string _activeCoreConfig = "";
    private readonly List<string> _allowedCoreConfigs = new();
    private readonly Dictionary<string, AgentCore> _coreInstances = new(StringComparer.OrdinalIgnoreCase);
    private ITool? _switchCoreTool;

    // ── 会话本体提示词（来自会话类型配置 PromptFiles，换核不变）──
    private string? _sessionPrompt;

    private MessageEngine? _messageEngine;

    /// <summary>图动作执行器（默认 null：图仍只观察，不发送）。</summary>
    private IGraphIntentDispatcher? _graphIntentDispatcher;
    private SessionEngine? _sessionEngine;
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
    private SessionPersistence? _persister;
    public void SetPersister(SessionPersistence p) => _persister = p;

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

    /// <summary>
    /// 配置本 session 可用的 Core 白名单并选定活跃核。取代旧的 SetAgentCore。
    /// 白名单来自会话类型配置（Storage/Session/{type}.json 的 AllowedCores），实例在列表内切换。
    /// 核实例懒建缓存：切核时 get-or-create new AgentCore(name)（fixedMode 锁配置）。
    /// </summary>
    public void ConfigureCores(IReadOnlyList<string>? allowedCoreConfigs, string defaultCore, string? activeOverride = null)
    {
        _allowedCoreConfigs.Clear();
        if (allowedCoreConfigs != null && allowedCoreConfigs.Count > 0)
            _allowedCoreConfigs.AddRange(allowedCoreConfigs);
        if (_allowedCoreConfigs.Count == 0)
            _allowedCoreConfigs.Add(defaultCore);

        var active = activeOverride != null
            && _allowedCoreConfigs.Contains(activeOverride, StringComparer.OrdinalIgnoreCase)
            ? activeOverride : defaultCore;
        if (!_allowedCoreConfigs.Contains(active, StringComparer.OrdinalIgnoreCase))
            active = _allowedCoreConfigs[0];

        _activeCoreConfig = active;
        _agentCore = GetOrCreateCore(active);
        _sessionPrompt ??= BuildSessionPrompt();
        AttachSessionPrompt(_agentCore);

        // 多核时注入 switch_core 工具（channel 黑名单默认放行；单核类型不注入，行为不变）
        if (_allowedCoreConfigs.Count > 1 && _switchCoreTool == null)
        {
            _switchCoreTool = new Tool.Core.SwitchCoreTool(this);
            if (!_tools.ContainsKey(_switchCoreTool.Name))
            {
                _tools[_switchCoreTool.Name] = _switchCoreTool;
                _activeTools.Add(_switchCoreTool.Name);
            }
        }
    }

    /// <summary>会话本体提示词（测试访问器）。</summary>
    internal string? SessionPrompt => _sessionPrompt;

    /// <summary>从会话类型配置的 PromptFiles 加载拼接提示词（Storage/Session/Prompts → Core/Prompts 双目录）。</summary>
    private string? BuildSessionPrompt()
    {
        var profile = SessionTypeProfiles.Get(Type);
        if (profile.PromptFiles == null || profile.PromptFiles.Length == 0) return null;
        var templatesDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates"));
        var parts = new List<string>();
        foreach (var file in profile.PromptFiles)
        {
            var content = PromptLoader.Load(file, PathConfig.StoragePath, templatesDir);
            if (!string.IsNullOrEmpty(content))
                parts.Add(content);
        }
        return parts.Count > 0 ? string.Join("\n\n", parts) : null;
    }

    /// <summary>把会话提示词绑定到核实例（换核不换提示词的关键注入点）。</summary>
    private void AttachSessionPrompt(AgentCore core)
    {
        if (core != null && !string.IsNullOrEmpty(_sessionPrompt))
            core.SessionPrompt = _sessionPrompt;
    }

    /// <summary>当前活跃 Core 配置名。</summary>
    public string ActiveCoreConfig => _activeCoreConfig;

    /// <summary>本 session 可用的 Core 配置名白名单（来自会话类型配置）。</summary>
    public IReadOnlyList<string> AllowedCoreConfigs => _allowedCoreConfigs;

    /// <summary>
    /// 运行时切换活跃 Core。模型（switch_core 工具）与外部（命令/WebUI）共用。
    /// 校验在白名单内 → 换核（懒建缓存）→ 注入系统消息 + 持久化 + 日志。
    /// </summary>
    public bool SwitchCore(string configName)
    {
        if (string.IsNullOrEmpty(configName)
            || !_allowedCoreConfigs.Contains(configName, StringComparer.OrdinalIgnoreCase))
            return false;

        _activeCoreConfig = configName;
        _agentCore = GetOrCreateCore(configName);
        AttachSessionPrompt(_agentCore);

        // 立即重设工具装配（外置切换发生在两轮之间也安全；轮中切换由 roundCore 硬化隔离）
        _agentCore.EngineType = _componentLoopType;
        _agentCore.AdditionalTools = _tools.Values.ToList();

        // 经 _pendingInjections 注入切换通知：下一轮开头 flush 进 _history 并持久化，
        // 不会像直接 _history.Add 那样被轮内 FormatAssistant 的 _history[^1] 覆盖。
        InjectExternal($"[核心切换] 已切换到 {_activeCoreConfig}，可用核心：{string.Join(", ", _allowedCoreConfigs)}");
        PersistStateAsync().GetAwaiter().GetResult();

        Signal.Event(LogGroup.Engine, "Session 切换 Core",
            new { sessionId = SessionId, core = _activeCoreConfig, allowed = _allowedCoreConfigs.ToArray() });
        return true;
    }

    /// <summary>静默换核（恢复 / compact 承接）：不注入历史消息、不持久化。非法名忽略。</summary>
    internal void SetActiveCore(string configName)
    {
        if (string.IsNullOrEmpty(configName)
            || !_allowedCoreConfigs.Contains(configName, StringComparer.OrdinalIgnoreCase))
            return;
        _activeCoreConfig = configName;
        _agentCore = GetOrCreateCore(configName);
        AttachSessionPrompt(_agentCore);
    }

    /// <summary>按配置名 get-or-create 核实例（懒建：首次切到该核才 new AgentCore）。</summary>
    private AgentCore GetOrCreateCore(string configName)
    {
        if (_coreInstances.TryGetValue(configName, out var existing))
            return existing;
        var core = new AgentCore(configName); // fixedMode 锁配置到该核
        // CallerTag 标识调用来源：Session:{id}（express/working）/ Task:{id}（task）。
        // 落进模型调用 span detail（caller）与 JSON 模型日志，token 统计据此归因。
        core.CallerTag = Type == "task" ? $"Task:{SessionId}" : $"Session:{SessionId}";
        _coreInstances[configName] = core;
        return core;
    }

    public void SetMessageEngine(MessageEngine me) => _messageEngine = me;
    public void SetGraphIntentDispatcher(IGraphIntentDispatcher dispatcher) => _graphIntentDispatcher = dispatcher;
    public void SetSessionEngine(SessionEngine se) => _sessionEngine = se;

    /// <summary>当前连接频道状态（注入组件 perLoopServices，供发送工具读取）。</summary>
    public ConnectedChannelState ConnectedChannel => _connected;

    /// <summary>
    /// 从 ComponentRegistry 加载指定组件，构建 ComponentHost。
    /// sessionId 为裸 GUID；loopType（权限组）用于组件白名单查找。
    /// </summary>
    public void InitComponents(string sessionId, ModuleBus moduleBus, IServiceProvider services, Action wakeLoop)
    {
        // 会话类型名即权限作用域（无 PermissionGroup 间接层）：组件装载 + 工具白黑名单都按 Type 走
        _componentLoopType = Type;
        _services = services;
        // 注入 per-session 连接状态：发送工具据此决定目标频道并校验发言权限
        var perLoopServices = new Dictionary<Type, object>
        {
            [typeof(IConnectedChannel)] = _connected
        };
        // 注入 spec 声明的 per-session 服务（如 review 会话的 IReviewStageAccess）
        if (_spec.SessionServices != null)
        {
            foreach (var (svcType, instance) in _spec.SessionServices)
                perLoopServices[svcType] = instance;
        }
        _componentHost = new ComponentHost(sessionId, _componentLoopType, moduleBus, services, wakeLoop, perLoopServices);
        _componentHost.InitAsync(_componentIds).GetAwaiter().GetResult();

        RefreshToolList();
    }

    /// <summary>
    /// session 工具表合并 Loop 组件工具 + ToolRegistry 全部注册工具（全局/独立工具）。
    /// 工具是否对当前会话可用由权限组（Storage/Session/{group}.json 黑白名单）在
    /// AgentCore 装配期 + ToolExecutor 执行期双重裁决，这里只做集合。
    /// </summary>
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
        // 合并 ToolRegistry 独立工具（权限组裁决在 AgentCore 装配期 + ToolExecutor 执行期）
        foreach (var (name, tool) in ToolRegistry.All)
        {
            if (!_tools.ContainsKey(name))
            {
                _tools[name] = tool;
                _activeTools.Add(name);
            }
        }
        // Phase 5 后注入的 compact 工具会出现在 _tools；Phase 3 暂不注入，保持空判断安全
        if (_compactTool != null && !_tools.ContainsKey("compact"))
        {
            _tools["compact"] = _compactTool;
            _activeTools.Add("compact");
        }
        // 多核会话的 switch_core 工具（权限裁决在 AgentCore 装配期 + ToolExecutor 执行期）
        if (_switchCoreTool != null && !_tools.ContainsKey(_switchCoreTool.Name))
        {
            _tools[_switchCoreTool.Name] = _switchCoreTool;
            _activeTools.Add(_switchCoreTool.Name);
        }
    }

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
        if (_sessionEngine != null)
        {
            _compactTool = new Tool.Core.CompactTool(this, _sessionEngine);
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
        Status = state.Status == SessionStatus.Crashed ? SessionStatus.Restored : state.Status;
        _isRestored = true;
        _createdAt = state.CreatedAt;
        _roundCount = state.RoundCount;
        WaitUntil = state.WaitUntil;
        _lastActivity = state.LastActivity;
        _history = history ?? new List<Message>();
        EffectiveParentId = state.EffectiveParentId;

        // 轴值恢复：按轴名逐条校验，未知轴与非法值丢弃、缺失的补默认（见 AxisStore.Reset）。
        // ⚠️ 丢弃必须记日志——否则一次手改 state.json 就变成"某条轴静默回到默认值"。
        var droppedAxes = _axisStore.Reset(state.Axes);
        if (droppedAxes.Count > 0)
        {
            var names = new List<string>();
            foreach (var d in droppedAxes) names.Add($"{d.Axis}:{d.Reason}");
            Signal.Warn(LogGroup.Engine, "轴值恢复时丢弃了非法项", new { sessionId = SessionId, dropped = names });
        }

        // 恢复连接频道状态（仅 Full 占用级，Listen 由 RestoreAll 单独重建订阅）
        if (!string.IsNullOrEmpty(state.ConnectedChannelId)
            && int.TryParse(state.ConnectedChannelId, out var cid)
            && !string.IsNullOrEmpty(state.ConnectedMode))
        {
            var mode = Enum.TryParse<ConnectionMode>(state.ConnectedMode, true, out var m)
                ? m : ConnectionMode.Full;
            // 真正的 claim + 订阅由 SessionEngine.RestoreAll 在 RestoreFromSnapshot 之前重建；
            // 这里仅恢复 ConnectedChannelState 内存状态
            _connected.Set(cid, mode);
        }
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

    /// <summary>
    /// 连接到频道。Full：claim 占用+停 express+订阅 normal+mention+可发言；Listen：仅订阅 normal、不占用、不可发言。
    /// 连接前先断开当前连接（独占单频道）。连接后载入该频道历史到下一轮上下文。
    /// </summary>
    public async Task ConnectChannelAsync(int channelId, ConnectionMode mode)
    {
        if (channelId <= 0 || _sessionEngine == null || _messageEngine == null) return;

        // 独占：先断开当前连接
        if (_connected.CurrentChannelId != 0)
            DisconnectChannel();

        var level = mode == ConnectionMode.Full ? ClaimLevel.Full : ClaimLevel.Listen;
        var claimed = _sessionEngine.ClaimChannel(SessionId, channelId, level);
        if (!claimed && mode == ConnectionMode.Full)
        {
            _pendingInjections.Add(new Message { Role = "user",
                Content = $"[系统] 连接频道 {channelId} 失败：该频道已被其他会话占用。" });
            return;
        }

        // 订阅消息：Full 收 normal+mention，Listen 仅收 normal
        var normalPort = _sessionEngine.SubscribeChannel(channelId, "normal", 5);
        _inputs[$"ch{channelId}:normal"] = normalPort;
        _connectedPorts.Add(normalPort);
        if (mode == ConnectionMode.Full)
        {
            var mentionPort = _sessionEngine.SubscribeChannel(channelId, "mention", 100);
            _inputs[$"ch{channelId}:mention"] = mentionPort;
            _connectedPorts.Add(mentionPort);
        }

        _connected.Set(channelId, mode);

        // 载入频道上下文（信息+参与者+历史）到下一轮
        if (_host != null)
        {
            var channel = await _sessionEngine.GetChannelByIdAsync(channelId);
            _host.AttachChannel(channelId, channel?.Name);
            try { _pendingInjections.AddRange(await _host.BuildChannelContextAsync(channelId)); }
            catch (Exception ex) { Signal.Warn(LogGroup.Engine, "连接频道历史载入失败", new { sessionId = SessionId, channelId, error = ex.Message }); }
        }

        Signal.Event(LogGroup.Engine, "Session 连接频道",
            new { sessionId = SessionId, channelId, mode = mode.ToString() });

        // 连接状态即时落地，避免崩溃丢失 claim（§5.9）
        PersistStateSync();
    }

    /// <summary>断开当前连接频道：退订端口、移除 InputPort、释放 claim、清状态、host 卸载。</summary>
    public void DisconnectChannel()
    {
        var channelId = _connected.CurrentChannelId;
        if (channelId == 0) return;

        foreach (var port in _connectedPorts)
            _messageEngine?.Unsubscribe(port);
        _connectedPorts.Clear();
        var toRemove = _inputs.Keys.Where(k => k.StartsWith($"ch{channelId}:")).ToList();
        foreach (var k in toRemove) _inputs.Remove(k);

        _sessionEngine?.ReleaseChannel(SessionId, channelId);
        _host?.DetachChannel(channelId);
        _connected.Clear();

        Signal.Event(LogGroup.Engine, "Session 断开频道", new { sessionId = SessionId, channelId });

        // 断开状态即时落地，避免崩溃后 claim 状态与存档不一致（§5.9）
        PersistStateSync();
    }

    /// <summary>同步持久化 state.json（连接/断开兜底，best-effort 不阻塞）。</summary>
    private void PersistStateSync()
    {
        if (_persister == null) return;
        try { _persister.SaveStateAsync(BuildStateSnapshot()).GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "连接状态持久化失败（不阻塞）",
                new { sessionId = SessionId, error = ex.Message });
        }
    }

    /// <summary>工具副作用结果：是否结束本突发 / 整个会话（类型见 <see cref="ToolEffectInterpreter"/>）。</summary>

    /// <summary>
    /// 执行初始化工具调用（bootstrap）。由 CreateSession 在 host 就绪后**同步**调用——
    /// claim/订阅/连接是入向路由前提，必须在 CreateSession 返回前生效，否则 escalate 后
    /// 新消息因 GetFullClaimer 命中不到而落回 ChannelEngine。结果系统消息缓存供 RunAsync 首轮消费。
    /// </summary>
    public async Task BootstrapAsync()
    {
        if (_initActions.Count == 0) return;

        var resolver = (Func<string, ITool?>)(name =>
            _tools.TryGetValue(name, out var t) ? t :
              _componentHost?.TryGetTool(name) ?? ToolRegistry.Get(name));
        var executor = new ToolExecutor(resolver)
        {
            PermissionGroup = _componentLoopType,
            SessionId = SessionId,
            ChannelId = _connected.CurrentChannelId,
        };
        var lines = new List<string>();

        foreach (var action in _initActions)
        {
            var results = await executor.ExecuteAsync(new List<ToolCall> { action });
            var result = results.Count > 0 ? results[0]
                : new ToolResult { Status = "failed", Error = "无结果" };
            await ApplyToolEffectAsync(action, result);

            var argStr = action.Inputs.Count > 0 ? string.Join(", ", action.Inputs) : "";
            var summary = result.IsSuccess
                ? DescribeInitResult(action.Tool, result)
                : $"失败：{result.Error ?? result.Status}";
            lines.Add($"- {action.Tool}({argStr}) → {summary}");
        }

        var msg = "[初始化] 你启动时执行了以下工具调用：\n" + string.Join("\n", lines);
        _bootstrapMessages.Add(new Message { Role = "user", Content = msg });
    }

    /// <summary>为初始化工具结果生成人类可读说明（连接类工具据当前连接状态描述）。</summary>
    private string DescribeInitResult(string tool, ToolResult result)
    {
        return tool switch
        {
            "connect_channel" => _connected.CanSpeak
                ? $"已连接到频道「{_connected.CurrentChannelId}」（可发言）"
                : $"连接频道 {result.Data} 未成功（可能已被占用）",
            "listen_channel" => $"已旁听频道「{_connected.CurrentChannelId}」（只看不说）",
            "disconnect_channel" => "已断开频道连接",
            _ => result.Data ?? "成功"
        };
    }

    /// <summary>
    /// 统一工具副作用分发。正常 agent 循环与 InitActions bootstrap 共用单一来源。
    /// speak/send_media 等 OutputOnly 工具自带发送，不在此重发。
    ///
    /// <para>⚠️ **判定已移到纯函数 <see cref="ToolEffectInterpreter"/>**：这里只做两件事——
    /// 把会话事实取成值（<see cref="BuildToolEffectContextAsync"/>），以及按顺序执行意图。
    /// 这样 step 不再直连 <c>_sessionEngine</c> / <c>_componentHost</c> / <c>_mind</c>（H1 之后的第二处解耦），
    /// 而"哪种工具结果会收尾／终结"第一次有了不建会话也能跑的测试。</para>
    /// </summary>
    private async Task<ToolEffect> ApplyToolEffectAsync(ToolCall call, ToolResult result)
    {
        var context = await BuildToolEffectContextAsync();
        var effect = ToolEffectInterpreter.Interpret(call, result, context, DateTime.UtcNow);

        var endSession = effect.EndSession;

        foreach (var action in effect.Actions)
        {
            switch (action)
            {
                case ToolEffectAction.SendMessage send:
                    await SendOutput(send.Text);
                    break;

                case ToolEffectAction.InjectContext inject:
                    InjectExternal(inject.Text);
                    break;

                case ToolEffectAction.SetWaitUntil wait:
                    SetWaitUntil(wait.Until);
                    break;

                case ToolEffectAction.WriteJournal journal:
                    await WriteJournalFromWaitAsync(journal.Raw);
                    break;

                case ToolEffectAction.ConnectChannel connect:
                    await ConnectChannelAsync(connect.ChannelId, connect.Mode);
                    break;

                case ToolEffectAction.DisconnectChannel:
                    DisconnectChannel();
                    break;

                case ToolEffectAction.CountForceExitAttempt:
                    _forceExitAttempts++;
                    break;

                case ToolEffectAction.Note note:
                    Signal.Event(LogGroup.Engine, note.Message, new { sessionId = SessionId });
                    break;

                case ToolEffectAction.Escalate escalate:
                    // 原实现：创建失败时把 EndSession 降回 false（会话继续跑），只记 warn。
                    if (_sessionEngine is null) { endSession = false; break; }
                    try
                    {
                        await _sessionEngine.CreateFromEscalateAsync(this, escalate.Reason);
                    }
                    catch (Exception ex)
                    {
                        Signal.Warn(LogGroup.Engine, "escalate 创建 working 失败",
                            new { sessionId = SessionId, error = ex.Message });
                        endSession = false;
                    }
                    break;
            }
        }

        return endSession == effect.EndSession ? effect : effect with { EndSession = endSession };
    }

    /// <summary>
    /// 把解释工具结果所需的会话事实取成值。⚠️ **保留原实现的短路顺序**：
    /// 只有在"没有在飞资源"时才会去问组件门（<c>QueryCanExitAsync</c>），
    /// 多问一次是行为差异，而这正是这类重构最容易悄悄改掉的地方。
    /// </summary>
    private async Task<ToolEffectContext> BuildToolEffectContextAsync()
    {
        var children = _sessionEngine?.GetActiveChildren(SessionId);
        var childCount = children?.Count ?? 0;
        var inFlightResources = _sessionEngine?.AnyResourceBlocksExit(SessionId) == true;

        var canExit = true;
        if (!inFlightResources)
            canExit = _componentHost is null || await _componentHost.QueryCanExitAsync();

        return new ToolEffectContext(Type, childCount, inFlightResources, canExit, _forceExitAttempts);
    }

    /// <summary>
    /// 把 wait 的 journal 参数写成经历（L2 + 情绪信号）。
    /// 支持 "[情绪:轴:目标值] 正文" 前缀标记，解析成 EmotionSignals。
    /// </summary>
    private async Task WriteJournalFromWaitAsync(string raw)
    {
        try
        {
            var mind = _sessionEngine?.Mind;
            if (mind?.Writer == null) return;

            var content = raw.Trim();
            string? signalsJson = null;

            // 解析 "[情绪:轴:目标值] 正文"（可多个，如 [情绪:joy:0.4] [情绪:excitement:0.3] 正文）
            if (content.StartsWith("[情绪:", StringComparison.Ordinal))
            {
                var signals = new List<string>();
                var start = 0;
                while (start < content.Length && content[start] == '[')
                {
                    var end = content.IndexOf(']', start);
                    if (end < 0) break;
                    var tag = content[(start + 1)..end];
                    var segs = tag.Split(':');
                    if (segs.Length == 3 && segs[0] == "情绪"
                        && float.TryParse(segs[2], System.Globalization.CultureInfo.InvariantCulture, out var target))
                    {
                        signals.Add($"{{\"Axis\":\"{segs[1]}\",\"Target\":{target.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"Reason\":\"wait journal\"}}");
                    }
                    start = end + 1;
                }
                content = content[start..].Trim();
                if (signals.Count > 0)
                    signalsJson = $"[{string.Join(",", signals)}]";
            }

            var req = new Mind.WriteJournalRequest
            {
                Content = content,
                Kind = "Experience",
                Source = "Session",
                EmotionSignalsJson = signalsJson,
                OccurredAt = DateTime.Now,
            };
            await mind.Writer.WriteBatchAsync(new Mind.WriteBatchRequest
            {
                Journals = new List<Mind.WriteJournalRequest> { req },
            });
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "wait journal 写入失败",
                new { sessionId = SessionId, error = ex.Message });
        }
    }

    /// <summary>
    /// 追加 Global 组件的 prompt 段（情绪/经历注入等）。Global 组件是单例，
    /// 每轮注入一次（带 LoopInfo 区分调用方）。注入段与 Loop 段合并进 [组件通知]。
    /// <para>取段与容错的实现在 <see cref="GlobalPromptSections.CollectPerRound"/>（上下文域），
    /// 这里只留一个薄壳保持调用点不变。</para>
    /// </summary>
    private void AppendGlobalPromptSections(List<string> sections) =>
        sections.AddRange(GlobalPromptSections.CollectPerRound(_services, SessionId, _componentLoopType));

    /// <summary>
    /// 追加 Global 组件的会话开头一次性静态参考段（技能清单等）。仅新会话首轮上下文构建时调用，
    /// 恢复会话跳过（参考段已随首轮持久化在存档历史中，不重复注入）。产物为独立用户消息，非 [组件通知]。
    /// </summary>
    private void AppendStartReferenceSections()
    {
        var section = GlobalPromptSections.CollectStartReference(_services, SessionId, _componentLoopType);
        if (section is null) return;

        _history.Add(new Message { Role = "user", Content = section });
        Signal.Debug(LogGroup.Engine, "会话开头参考注入(一次性)",
            new { sessionId = SessionId, sectionLen = section.Length });
    }

    /// <summary>
    /// 应用上下文层产出的变更意图。**这是唯一允许改工具表的地方**（见 <see cref="ContextMutation"/>）。
    ///
    /// <para>⚠️ 原来这几行写在压缩块里，直接 `_tools.Clear()` 并改 `_agentCore.AdditionalTools`。
    /// 那是一条从上下文层到工具层的反向数据流：拆模块后它会变成看不见的回指，
    /// 而且"谁能改工具表"会散在两个地方。现在意图与应用分离，改法只有这一处。</para>
    /// </summary>
    private void ApplyContextMutation(ContextMutation mutation)
    {
        switch (mutation)
        {
            case LockToolTableTo lockTo:
                // 调用方已判过 _compactTool 非空与 _preLockdownTools 为 null；这里保留一次防御性返回。
                if (_compactTool is null) return;

                _preLockdownTools = new Dictionary<string, ITool>(_tools, StringComparer.OrdinalIgnoreCase);
                _tools.Clear();
                _activeTools.Clear();
                _tools[lockTo.ToolName] = _compactTool;
                _activeTools.Add(lockTo.ToolName);
                if (_agentCore != null)
                    _agentCore.AdditionalTools = new List<ITool> { _compactTool };
                Signal.Event(LogGroup.Engine, "L3 锁定：工具表只剩 " + lockTo.ToolName,
                    new { sessionId = SessionId, preLockdownCount = _preLockdownTools.Count });
                break;
        }
    }

    // ── 组件管理 ──
    // 当前 _componentHost 在 InitComponents 时一次性构建，运行期无法增删组件实例并重建工具表。
    // 真正实现需要：实例化新组件 → 注册其工具到 _componentHost → RefreshToolList。
    // 在落地前抛异常而非静默改 _componentIds，避免调用方误以为组件已生效。
    public void EnableComponent(string componentId)
        => throw new NotSupportedException("运行时动态启用组件尚未实现，组件须在 CreateSessionSpec.ComponentIds 中预先声明");

    public void DisableComponent(string componentId)
        => throw new NotSupportedException("运行时动态卸载组件尚未实现，组件须在 CreateSessionSpec.ComponentIds 中预先声明");

    /// <summary>运行时翻转已装载组件实例的启用状态（仅对已在组件清单中的实例生效；新增组件须预先声明，不即时装载）。</summary>
    internal async Task ToggleComponentAsync(string name, bool enabled)
    {
        if (_componentHost == null) return;
        if (enabled)
            await _componentHost.EnableComponentAsync(name);
        else
            await _componentHost.DisableComponentAsync(name);
    }

    public void RequestStop(string reason)
    {
        Status = SessionStatus.Completed;
        _cts?.Cancel();
    }

    /// <summary>测试退出清理用：静默置 Completed（不取消循环、不触发 OnCompleted/Resurrector），仅改状态供落盘。</summary>
    internal void MarkCompletedForCleanup() => Status = SessionStatus.Completed;

    public void SetWaitUntil(DateTime? until) => WaitUntil = until;
    public void TouchActivity() => _lastActivity = DateTime.UtcNow;
    /// <summary>外部信号唤醒（由 wakeLoop 回调触发，如异步任务完成）。</summary>
    public void WakeUp() => _wakeSignaled = true;

    /// <summary>
    /// 恢复后的 wait 到期重调度专用：注入 wait timeout 提示并唤醒。
    /// 与 WaitForActivationAsync 的到期检查（InjectMessage timeout）产出一致，
    /// 但不碰 WaitForActivationAsync 内部超长 Task.Delay（Task.Delay 数值溢出会抛
    /// TimeOutException，把会话打 Crashed——历史超长 wait 恢复的已知坑）。
    /// </summary>
    internal void InjectWaitTimeoutExpired()
    {
        InjectMessage(new { type = "timeout", wait_reason = "wait timeout expired" });
        _wakeSignaled = true;
    }

    /// <summary>
    /// 外部直接注入系统消息（如子 session 回报结果、wait timeout 提示等）。
    /// 写入 _pendingInjections，由下一轮 agent 推理前消费；同时触发 WakeUp 唤醒等待中的 session。
    /// </summary>
    internal void InjectExternal(string content)
    {
        _pendingInjections.Add(new Message { Role = "user", Content = content });
        _wakeSignaled = true;
        Signal.Debug(LogGroup.Engine, "外部消息注入",
            new { sessionId = SessionId, contentLen = content.Length });
    }

    /// <summary>当前累计轮次（供 SubAgentAccessAdapter 查询子 session 状态）。</summary>
    internal int RoundCountInternal => _roundCount;
    /// <summary>创建时间（供 SubAgentAccessAdapter 查询存活时长）。</summary>
    internal DateTime CreatedAtInternal => _createdAt;
    /// <summary>最后活动时间（供 SubAgentAccessAdapter 查询）。</summary>
    internal DateTime LastActivityInternal => _lastActivity;

    /// <summary>提取 session 最终输出（最后一条 assistant 消息文本）。供子 session 完成通知使用。</summary>
    internal string? ExtractFinalOutput()
    {
        for (int i = _history.Count - 1; i >= 0; i--)
        {
            var m = _history[i];
            if (m.Role == "assistant" && !string.IsNullOrEmpty(m.Content))
                return m.Content;
        }
        return null;
    }

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

                _agentCore!.EngineType = _componentLoopType;  // 权限组（channel/task）
                _agentCore!.AdditionalTools = _tools.Values.ToList();

                // 硬化：本轮锁定的核引用。外置切换（命令/WebUI）可能在本轮中途发生，
                // 整轮用 roundCore 保证格式化标志（UseNativeTools 等）与产出本轮的核一致。
                var roundCore = _agentCore;

                agentRound++;

                // 中途注入：每轮推理前检查端口是否有新消息，有就立即注入上下文（不阻塞等待）
                {
                    var midDrain = DrainInputPortsRaw(CancellationToken.None);
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

                // 每轮推理一条 span，承载模型调用 + 工具执行的耗时与结果
                using var roundSpan = Signal.Open(LogGroup.Engine, $"Session 轮次 #{agentRound}",
                    new { sessionId = SessionId, round = agentRound, toolCount = _tools.Count });

                ModelOutput output;
                try
                {
                    // 只读的线上请求体捕获（默认关闭；影子模式下打开）。必须在调用**之前**开作用域。
                    Phase = StepPhase.Inferring;
                    var wire = Client.WireRequestCapture.Begin();
                    output = await roundCore.InvokeAsync(_history, EngineMode.Working);
                    // ⚠️ 影子快照必须紧贴调用之后：下一次 InvokeAsync 会重设客户端历史。
                    // 默认 Off 时这一步是空操作，Legacy 路径一个字节都不变。
                    Brain.SessionBrainRuntime.Observe(
                        roundCore, SessionId, _componentLoopType, PathConfig.StoragePath, wire.Body);
                }
                catch (Exception ex)
                {
                    Signal.Warn(LogGroup.Model, "Session 模型调用失败", new { sessionId = SessionId, round = agentRound, error = ex.Message });
                    _history.Add(new Message { Role = "system", Content = $"[错误] 模型调用失败: {ex.Message}" });
                    continue;
                }

                _history.Add(new Message { Role = "assistant", Content = output.Text ?? output.Thinking ?? "" });

                // ── 工具执行 ──
                Phase = StepPhase.ExecutingTools;
                if (output.HasToolCalls && output.ToolCalls != null && output.ToolCalls.Count > 0)
                {
                    System.Func<string, ITool?> resolver = name =>
                        _tools.TryGetValue(name, out var t) ? t :
                          _componentHost?.TryGetTool(name) ?? ToolRegistry.Get(name);
                    var executor = new ToolExecutor(resolver)
                    {
                        PermissionGroup = _componentLoopType,
                        SessionId = SessionId,
                        ChannelId = _connected.CurrentChannelId,
                    };

                    var results = await executor.ExecuteAsync(output.ToolCalls);
                    roundSpan.SetCloseDetail(new
                    {
                        sessionId = SessionId,
                        round = agentRound,
                        tools = output.ToolCalls.Select((c, i) => new { tool = c.Tool, status = results[i].Status })
                    });

                    // assistant 轮：保留 tool_use ContentParts（携带 tool_use_id），与下方 tool_result 严格配对
                    // 替换上面那条空文本 assistant，避免模型看不到自己调用了工具（runaway 根因）
                    _history[_history.Count - 1] = NativeToolMessageFormatter.FormatAssistant(
                        output, roundCore.UseNativeTools, resolver);

                    // tool_result 轮：一条消息按 tool_use_id 配对全部 call→result
                    var resultMsg = NativeToolMessageFormatter.FormatToolResults(
                        output.ToolCalls, results, roundCore.UseNativeTools, !roundCore.DisableImageMessages);
                    _history.Add(resultMsg);

                    // 每轮持久化：assistant turn + tool_result turn 一起写
                    await PersistRoundAsync(new List<Message>
                    {
                        _history[_history.Count - 2],
                        _history[_history.Count - 1]
                    });

                    // 副作用 + 停止判定（突发结束 ≠ 会话结束）
                    Phase = StepPhase.Judging;
                    // endBurst：本次突发收尾，清消息回 WaitForActivationAsync，session 保活握 claim
                    // endSession：会话真正终结，break 走 finally 释放 claim
                    var endBurst = false;
                    var endSession = false;
                    for (int i = 0; i < output.ToolCalls.Count; i++)
                    {
                        var effect = await ApplyToolEffectAsync(output.ToolCalls[i], results[i]);
                        if (effect.EndBurst) endBurst = true;
                        if (effect.EndSession) endSession = true;
                    }

                    // 内联 wait（speak 含 [wait] 占位符时 result.RequestWait=true）：挂起本突发
                    if (results.Any(r => r.RequestWait))
                        endBurst = true;

                    if (endSession)
                    {
                        _isBusy = false;
                        Signal.Event(LogGroup.Engine, "endSession：退出循环释放 claim", new { sessionId = SessionId });
                        endReason = "end_session";
                        break;
                    }
                    if (endBurst)
                    {
                        _isBusy = false;
                        Signal.Event(LogGroup.Engine, "endBurst：回等待保活",
                            new { sessionId = SessionId, historyCount = _history.Count, waitUntil = WaitUntil });
                        _forceExitAttempts = 0;
                        awaitingInput = true;
                        Phase = StepPhase.Waiting;
                        continue;
                    }
                }
                else
                {
                    // 模型无工具调用：注入提醒继续循环（Working session 不应自动挂起）
                    Signal.Event(LogGroup.Engine, "无工具调用，注入提醒继续",
                        new { sessionId = SessionId, round = agentRound });
                    var reminder = new Message { Role = "user", Content = "[系统] 你没有调用任何工具。如果任务完成请用 end_session 结束，需要等人回复用 wait 挂起，否则继续工作。" };
                    _history.Add(reminder);
                    // assistant + reminder 一起写
                    await PersistRoundAsync(new List<Message>
                    {
                        _history[_history.Count - 2],
                        _history[_history.Count - 1]
                    });
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
    /// session 结束后 ExpressEngine 从同一游标恢复，不重读 session 已处理的消息。
    /// </summary>
    private void AdvanceChannelCursor(int channelId, int maxMessageId)
    {
        if (channelId <= 0 || maxMessageId <= 0) return;
        try { new ChannelContextPersistence(channelId).AdvanceCursor(maxMessageId); }
        catch { /* best-effort，落地失败不中断会话 */ }
    }


    /// <summary>
    /// 从所有输入端口收集消息，转换为 (channelId, UserMessage, isMention) 元组列表。
    /// EngineIncomingMessage → UserMessage：最小化转换（无 SenderName，但含 ChannelId/Content/UserId/PlatformMessageId）。
    /// isMention：消息来自 "mention" 端口（Weight >= 100，被 @ 提及）。
    /// </summary>
    private List<(int channelId, UserMessage msg, bool isMention)> DrainInputPortsRaw(CancellationToken ct)
    {
        var result = new List<(int channelId, UserMessage msg, bool isMention)>();
        foreach (var (name, port) in _inputs)
        {
            bool isMentionPort = name.Contains("mention", StringComparison.OrdinalIgnoreCase);
            while (port.TryPeek(out var data))
            {
                port.ReceiveAsync(ct).GetAwaiter().GetResult();
                if (data is UserMessage existing)
                {
                    result.Add((existing.ChannelId, existing, isMentionPort));
                }
                else if (data is EngineIncomingMessage em)
                {
                    var um = new UserMessage
                    {
                        ChannelId = em.ChannelId,
                        UserId = em.SenderUserId,
                        Content = em.Content,
                        PlatformMessageId = em.MessageId,
                        Time = em.Timestamp == default ? DateTime.UtcNow : TimeUtil.NormalizeToUtc(em.Timestamp),
                        IsFromBot = false,
                        SenderName = ""
                    };
                    result.Add((em.ChannelId, um, isMentionPort));
                }
                else if (data is string s)
                {
                    var um = new UserMessage { Content = s, ChannelId = 0, Time = DateTime.UtcNow };
                    result.Add((0, um, false));
                }
                else if (data != null)
                {
                    var um = new UserMessage { Content = JsonConvert.SerializeObject(data), ChannelId = 0, Time = DateTime.UtcNow };
                    result.Add((0, um, false));
                }
            }
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
        var activated = await WaitForActivationCoreAsync(ct);
        _activationShadow?.Compare(activated);
        return activated;
    }

    /// <summary>原判定逻辑（影子对比期间保持不变）。</summary>
    private async Task<bool> WaitForActivationCoreAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _activationShadow?.Observe(_inputs);
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

            // ⑤ 外部唤醒信号（异步任务完成/跨循环通知）
            if (_wakeSignaled)
            {
                _wakeSignaled = false;
                Signal.Debug(LogGroup.Engine, "WaitForActivation 唤醒：外部信号", new { sessionId = SessionId });
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
        var text = data is string s ? s : Newtonsoft.Json.JsonConvert.SerializeObject(data);
        _pendingInjections.Add(new Message { Role = "user", Content = $"[系统] {text}" });
    }

    // ── 持久化 ──

    /// <summary>
    /// 每轮持久化：追加新增消息到 history.jsonl + 原子重写 state.json。
    /// best-effort，失败不阻塞会话循环。
    /// </summary>
    private async Task PersistRoundAsync(List<Message> newMessages)
    {
        if (_persister == null) return;
        try
        {
            await _persister.AppendHistoryAsync(newMessages);
            await _persister.SaveStateAsync(BuildStateSnapshot());
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "持久化失败（不阻塞）",
                new { sessionId = SessionId, error = ex.Message, newCount = newMessages.Count });
        }
    }

    /// <summary>构造 state.json 快照对象。</summary>
    internal SessionStateSnapshot BuildStateSnapshot() => new()
    {
        SessionId = SessionId,
        Status = Status,
        CreatedAt = _createdAt,
        RoundCount = _roundCount,
        WaitUntil = WaitUntil,
        LastActivity = _lastActivity,
        CompressionTier = _compression?.CurrentTier.ToString() ?? "None",
        ActiveCoreConfig = _activeCoreConfig,
        ConnectedChannelId = _connected.CurrentChannelId > 0 ? _connected.CurrentChannelId.ToString() : null,
        ConnectedMode = _connected.Mode != ConnectionMode.None ? _connected.Mode.ToString() : null,
        Axes = _axisStore.Snapshot(),
        ChannelCursors = BuildChannelCursors(),
        ChildSessionIds = _childSessionIds.ToArray(),
        EffectiveParentId = EffectiveParentId,
        ForwardedTo = ForwardedTo
    };

    /// <summary>从 host 维护的各频道 state 取 LastConsumedMessageId 构造游标快照。</summary>
    private Dictionary<int, int> BuildChannelCursors()
    {
        var cursors = new Dictionary<int, int>();
        if (_host == null) return cursors;
        foreach (var cid in _host.ChannelIds)
        {
            try
            {
                var c = _host.StateFor(cid).LastConsumedMessageId;
                if (c > 0) cursors[cid] = c;
            }
            catch { /* skip */ }
        }
        return cursors;
    }

    private static int EstimateChars(Message m)
    {
        if (m.Content != null)
            return m.Content.Length;
        if (m.ContentParts != null)
            return m.ContentParts.Sum(p =>
                (p.Text?.Length ?? 0) + (p.ToolInput?.Length ?? 0) + (p.ToolName?.Length ?? 0));
        return 0;
    }

    public async Task PersistStateAsync()
    {
        if (_persister == null)
        {
            // 兼容路径：无 persister 时走旧 meta.json 写入（用于优雅退出场景的兜底）
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
            return;
        }
        await _persister.SaveStateAsync(BuildStateSnapshot());
    }

    // ── 父子链辅助方法 ──

    internal void RemoveChildFromState(string childId)
    {
        lock (_childSessionIds) _childSessionIds.Remove(childId);
        _persister?.SaveStateAsync(BuildStateSnapshot()).GetAwaiter().GetResult();
    }

    internal void AddChild(string childId)
    {
        lock (_childSessionIds) _childSessionIds.Add(childId);
    }

    internal string[] GetComponentIdsForCompact() => _componentIds.ToArray();
}
