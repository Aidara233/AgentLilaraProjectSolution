// AgentCoreProcessor/Engine/Session/SessionEngine.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Engine.Messaging;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Session;

/// <summary>
/// Session 管理器。30s 监控循环：WaitUntil 超时、ScheduledTask 触发、TODO 扫描。
/// </summary>
internal class SessionEngine : ISubEngine, AgentLilara.PluginSDK.Services.ISessionResourceAccess
{
    public string EngineType => "Session";
    public bool IsAlive { get; private set; } = true;
    public bool IsInfrastructure => true;

    private readonly ISystemContext _ctx;
    private readonly ConcurrentDictionary<string, RuntimeSession> _sessions = new();
    /// <summary>顶层资源池：channelId → 池。频道占用状态在池里（Full claim + Listen 旁听）。</summary>
    private readonly ConcurrentDictionary<int, SessionPool> _pools = new();
    /// <summary>sessionId → 所在池（含全部 member：活跃 + 被 compact 换掉的旧 id）。寻址权威索引。</summary>
    private readonly ConcurrentDictionary<string, SessionPool> _poolByMember = new();
    private readonly List<ScheduledTask> _scheduledTasks = new();
    private readonly List<TodoItem> _todoItems = new();
    private ICurrencyService? _currency;
    private DateTime _lastTodoScan = DateTime.MinValue;

    // session-scoped 子资源注册表（子 agent / 下载 / SSH 共用）
    private readonly SessionResourceRegistry _sessionResources = new();

    /// <summary>RestoreAll 完成信号。消息路由在检查 claim 前等待此信号，确保恢复的 claim 已落地。</summary>
    private readonly TaskCompletionSource _restoreTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>等待 RestoreAll 完成（带超时）。超时后放行，避免 SessionEngine 异常时阻塞所有消息路由。</summary>
    public async Task WaitRestoredAsync(TimeSpan timeout)
    {
        var done = _restoreTcs.Task;
        var winner = await Task.WhenAny(done, Task.Delay(timeout));
        if (winner != done)
            Signal.Warn(LogGroup.Engine, "等待 RestoreAll 超时，放行消息路由", new { timeout = timeout.TotalSeconds });
    }

    public SessionEngine(ISystemContext ctx)
    {
        _ctx = ctx;
    }

    private void OnRuntimeSessionCompleted(IRuntimeSession session)
    {
        _ctx.Graphs?.ReleaseSession(session.SessionId);
        Signal.Event(LogGroup.Engine, "Graph session mount released", new { session.SessionId });
    }

    /// <summary>只绑定精确的 session-scope 挂载；没有挂载时保持 null。</summary>
    /// <summary>只绑定精确匹配本会话的 session-scope 挂载（可多张）；没有挂载时为空集合。</summary>
    private void InitializeGraphMount(RuntimeSession session) =>
        session.SetGraphRunners(
            _ctx.Graphs?.RunnersForSession(session.SessionId) ?? Array.Empty<KeywordGraphRunner>());

    /// <summary>心智引擎（统一写入口 + 情绪 + 可信度服务）。</summary>
    public Mind.MindEngine? Mind => _ctx.Mind;

    public void SetCurrency(ICurrencyService currency) => _currency = currency;

    // ── Session CRUD ──

    public IRuntimeSession CreateSession(CreateSessionSpec spec)
        => CreateSession(spec, fixedSessionId: null);

    /// <summary>
    /// 创建会话。fixedSessionId 仅供 compact 承接用（新 session 在创建前已由池 TakeOver 预定 id，
    /// 必须与池中一致，否则 connect_channel 的幂等 claim 不命中）。其余路径传 null（自动生成）。
    /// </summary>
    internal IRuntimeSession CreateSession(CreateSessionSpec spec, string? fixedSessionId)
    {
        var profile = SessionTypeProfiles.Get(spec.Type);
        // 计算 finalComponents
        var finalComponents = ComputeFinalComponents(spec, profile);

        var session = fixedSessionId != null
            ? new RuntimeSession(spec, fixedSessionId, finalComponents)
            : new RuntimeSession(spec, finalComponents);
        session.SetCurrency(_currency!);
        session.SetSessionEngine(this);
        session.OnCompleted += OnRuntimeSessionCompleted;
        InitializeGraphMount(session);

        // 复活善后：订阅 session 完成事件（心智引擎整理写入）
        if (Mind?.Resurrector != null)
            Mind.Resurrector.Attach(session);

        // 会话身份统一为裸 sessionId（LoopId 概念已移除，channel:N 前缀不再产生）。
        var sessionId = session.SessionId;

        // 加载组件
        try
        {
            session.InitComponents(
                sessionId,
                _ctx.ModuleBus,
                _ctx.ComponentServices,
                () => session.WakeUp());
        }
        catch (Exception ex)
        {
            // 组件加载失败不阻塞 Session 创建
            Signal.Error(LogGroup.Engine, "Session 组件初始化失败", new { sessionId = session.SessionId, error = ex.Message });
        }

        _sessions[session.SessionId] = session;

        // 注入多核配置（白名单 + 默认核）和 MessageEngine
        session.ConfigureCores(profile.AllowedCoreConfigs, profile.DefaultCoreConfig);
        var messageEngine = _ctx.GetActiveEnginesSnapshot()
            .OfType<Messaging.MessageEngine>().FirstOrDefault();
        if (messageEngine != null) session.SetMessageEngine(messageEngine);

        // 根据 PortSpec 创建输入端口并订阅 MessageEngine
        foreach (var (portName, portSpec) in spec.InputPorts)
        {
            if (messageEngine != null)
            {
                var port = messageEngine.Subscribe(portSpec.ChannelId, portSpec.SubStream, portSpec.Weight);
                session.AddInputPort(portName, port);
            }
        }

        // 自动 ClaimChannel
        if (spec.AutoClaimChannel && spec.InputPorts.Count > 0)
        {
            var firstChannel = spec.InputPorts.First().Value.ChannelId;
            var claimed = ClaimChannel(session.SessionId, firstChannel, ClaimLevel.Full);
            // 同步连接状态：speak 目标频道 + escalate 读 CurrentChannelId 都依赖 _connected。
            // 无 connect_channel init-action 时（express 直连端口），此处是唯一设置点。
            if (claimed)
                session.ConnectedChannel.Set(firstChannel, ConnectionMode.Full);
            Signal.Event(LogGroup.Engine, "Session AutoClaim 频道",
                new { sessionId = session.SessionId, channelId = firstChannel, claimed, claimer = GetFullClaimer(firstChannel) });
        }

        // 构建 SessionAgentHost（同步）：InitActions 的 connect_channel 需要 host 已就绪才能挂载频道上下文
        try
        {
            var channelPorts = new List<(int channelId, string? channelName, bool preload)>();
            foreach (var (_, portSpec) in spec.InputPorts)
            {
                var ch = _ctx.Session.GetChannelByIdAsync(portSpec.ChannelId).GetAwaiter().GetResult();
                channelPorts.Add((portSpec.ChannelId, ch?.Name, portSpec.PreloadHistory));
            }

            var dataSource = new SystemContextDataSource(_ctx);
            var svc = new ChannelContextService(dataSource);
            var initialStateText = spec.InitialState is string s ? s
                : spec.InitialState != null ? Newtonsoft.Json.JsonConvert.SerializeObject(spec.InitialState)
                : null;
            var host = new SessionAgentHost(channelPorts, initialStateText, svc);
            session.SetAgentHost(host);
            Signal.Event(LogGroup.Engine, "Session AgentHost 就绪",
                new { sessionId = session.SessionId, channelPorts = channelPorts.Count });
        }
        catch (Exception ex)
        {
            Signal.Error(LogGroup.Engine, "Session AgentHost 初始化失败", new { sessionId = session.SessionId, error = ex.Message });
        }

        // 执行 InitActions bootstrap（同步）：claim/订阅/连接是入向路由前提，必须在 CreateSession
        // 返回前生效，否则触发者（escalate）返回后新消息因 claim 未建立而落回 ChannelEngine。
        try
        {
            session.BootstrapAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Signal.Error(LogGroup.Engine, "Session Bootstrap 失败", new { sessionId = session.SessionId, error = ex.Message });
        }

        // 写 spec.json + 注入 persister（持久化三件套就绪）
        // spec.json 是不可变存档，创建时一次写入；state.json 由 RunAsync 内每轮原子重写
        try
        {
            var persister = new SessionPersistence(session.SessionId);
            persister.SaveSpecAsync(spec, DateTime.UtcNow).GetAwaiter().GetResult();
            session.SetPersister(persister);
        }
        catch (Exception ex)
        {
            Signal.Warn(LogGroup.Engine, "Session spec.json 写入失败（持久化降级）",
                new { sessionId = session.SessionId, error = ex.Message });
        }

        // 启用压缩（L1/L2 文字注入；L3 锁定等 Phase 5 注入 CompactTool 后自动激活）
        session.InitCompression();

        // 启动 Session
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource();
                await session.RunAsync(cts.Token);
                Signal.Event(LogGroup.Engine, "Session RunAsync 返回，即将释放 claim",
                    new { sessionId = session.SessionId, status = session.Status.ToString() });
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "Session 崩溃",
                    new { sessionId = session.SessionId, error = ex.Message, stack = ex.StackTrace });
            }
            finally
            {
                // Session 结束时释放占用
                ReleaseSessionClaims(session.SessionId);
                Signal.Event(LogGroup.Engine, "Session claims 已释放", new { sessionId = session.SessionId });
            }
        });

        // 池树登记（仅当 ParentSessionId 非空）：子 agent → 新建子池挂到父会话所在池。
        // compact 场景（fixedSessionId 已在创建前 TakeOver 进同池）_poolByMember 已含新 id → 跳过。
        if (!string.IsNullOrEmpty(spec.ParentSessionId)
            && !_poolByMember.ContainsKey(session.SessionId))
            CreateSubPool(session.SessionId, spec.ParentSessionId);

        return session;
    }

    /// <summary>
    /// 计算会话的最终组件清单（CreateSession 与 RestoreAll 共用，保证恢复装配与新建一致）。
    /// ResolvedComponentIds（compact 承接）优先；否则类型文件 Components ± Additional/Excluded。
    /// </summary>
    private static string[] ComputeFinalComponents(CreateSessionSpec spec, SessionTypeProfile profile)
    {
        if (spec.ResolvedComponentIds != null)
            return spec.ResolvedComponentIds;
        // 组件清单即会话类型的唯一装配源（Storage/SessionTypes/{type}.json Components）
        var set = new HashSet<string>(profile.Components, StringComparer.OrdinalIgnoreCase);
        if (spec.AdditionalComponents != null) set.UnionWith(spec.AdditionalComponents);
        if (spec.ExcludedComponents != null) set.ExceptWith(spec.ExcludedComponents);
        return set.ToArray();
    }

    /// <summary>
    /// 为子 agent 创建子池：挂到父会话所在的池（直接父池），子池 ActiveSession = 子 agent。
    /// 子池 ChannelId=0（无频道、不持 claim），独享自己的池。
    /// </summary>
    internal void CreateSubPool(string childId, string parentSessionId)
    {
        lock (_poolLock)
        {
            if (_poolByMember.ContainsKey(childId)) return;
            if (!_poolByMember.TryGetValue(parentSessionId, out var parentPool)) return;
            var sub = new SessionPool(0) { Parent = parentPool };
            sub.Claim(childId);
            parentPool.ChildPools.Add(sub);
            _poolByMember[childId] = sub;
        }
    }

    /// <summary>
    /// compact 工具专属：旧 session 调 compact(summary) 触发新会话承接。
    /// 新 SessionId（新 GUID），ParentSessionId = 旧 sessionId，
    /// InitialState = "[上一会话摘要]\n{summary}\n\n请继续工作"，轮次接续旧 session。
    /// 同步生效：ClaimChannel 在 CreateSession 内同步完成，新 session 立马接管频道。
    /// </summary>
    public async Task<IRuntimeSession> CreateFromCompact(RuntimeSession oldSession, string summary)
    {
        // 同池换活跃 session：找到旧 session 所在的池，把活跃 session 预定为新 session id。
        // 占用状态原地不动（不释放、不重取）——这是"占用状态在池里"的核心语义。
        // 新 session 创建后 connect_channel 会幂等命中（SessionPool.Claim 同 id 重入成功），
        // 不再有 claim 短暂释放的中间态（原 ReleaseSessionClaims → 重新 claim）。
        var newId = Guid.NewGuid().ToString("N")[..12];
        var oldPool = FindPoolBySession(oldSession.SessionId);
        oldPool?.TakeOver(newId);

        // spec.InputPorts 留空：build port spec 全靠 InitActions.connect_channel 在 BootstrapAsync
        // 阶段一次性完成 claim + 订阅 + 频道上下文载入 —— 与 escalate 路径行为对齐，
        // 避免重复订阅（spec.InputPorts 订阅一次 + connect_channel 又订阅一次 = 4 个端口/2 冗余）。
        // AutoClaimChannel=true 仅在 spec.InputPorts 非空时才生效，这里保持 true 但不触发。
        // RestoreAll 读 spec.InputPorts 时仍能从 state.json 的 ConnectedChannelId 回退重建端口
        // （上一轮已修复）。
        var oldSpec = new CreateSessionSpec
        {
            Type = oldSession.Type,
            ResolvedComponentIds = oldSession.GetComponentIdsForCompact(),
            AutoClaimChannel = true,
            InputPorts = new Dictionary<string, PortSpec>(StringComparer.OrdinalIgnoreCase),
            OutputPorts = new Dictionary<string, string>(),
            InitialState = $"[上一会话摘要]\n以下是上一会话的总结，仅供背景参考（压缩动作已完成，你是新 compact 接管的新会话；不要再调用 compact 总结同一会话，也不要用 escalate，你已经在工作模式里）：\n{summary}\n\n请基于上述总结继续未完成的工作。",
            InitActions = new List<ToolCall>(oldSession.GetInitActionsForCompact()),
            ParentSessionId = oldSession.SessionId,
        };

        var newSession = (RuntimeSession)CreateSession(oldSpec, fixedSessionId: newId);

        // 承接旧 session 的活跃核（同类型白名单必然包含）
        newSession.SetActiveCore(oldSession.ActiveCoreConfig);

        // 接续旧 session 的轮次计数，避免新 session 从 0 重新跑 free round
        newSession.InheritRoundCount(oldSession.GetRoundCountForCompact());

        // 子链自动跟随：子 agent 池的 Parent 指向池对象（不指向 session id），compact 换活跃 session
        // 后子 agent 找父自动拿到新 session，无需迁移。仅持久化子 session 的 effectiveParentId 到 state.json
        // （供重启后子链重建参考）。
        foreach (var childId in (GetActiveChildren(oldSession.SessionId) ?? Array.Empty<string>()))
        {
            try
            {
                var childPersister = new SessionPersistence(childId);
                childPersister.UpdateEffectiveParentAsync(childId, newSession.SessionId).GetAwaiter().GetResult();
            }
            catch { }
        }

        // 通知所有 ISessionResource：loopId 从旧 session rebind 到新 session
        await _sessionResources.NotifyCompactingAsync(oldSession.SessionId, newSession.SessionId);

        Signal.Event(LogGroup.Engine, "compact 创建新 session",
            new { oldId = oldSession.SessionId, newId = newSession.SessionId, summaryLen = summary.Length });

        return newSession;
    }

    /// <summary>按活跃 session 查找其所在池（compact TakeOver 用）。无池返回 null。</summary>
    private SessionPool? FindPoolBySession(string sessionId)
    {
        lock (_poolLock)
            return _poolByMember.TryGetValue(sessionId, out var pool) ? pool : null;
    }

    /// <summary>
    /// 为频道创建 express 会话（频道层调度：无占用消息到达 / 冷启动）。
    /// express 会话 AutoClaim Full → 顶层池持有 claim。频道已被会话占用则返回 null。
    /// 直接 claim + 订阅 normal/mention 端口（不走 connect_channel init-action——
    /// 该工具需 permission 白名单且与 AutoClaim 重复；express 是轻量被动响应，端口即连接）。
    /// 会话身份/模式提示词由 SessionTypeProfile.PromptFiles（express_workflow.txt）加载，InitialState 留空。
    /// </summary>
    /// <summary>图路由：优先复用频道当前活跃会话；频道空闲时按图指定类型创建，再投递已入库消息。</summary>
    internal bool RouteGraphMessage(int channelId, string sessionType, UserMessage message, bool isMentioned)
    {
        if (channelId <= 0 || string.IsNullOrWhiteSpace(sessionType)) return false;

        var messageEngine = _ctx.GetActiveEnginesSnapshot()
            .OfType<Messaging.MessageEngine>().FirstOrDefault();
        if (messageEngine == null) return false;

        SessionTypeProfile profile;
        try
        {
            profile = SessionTypeProfiles.Get(sessionType);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            Signal.Warn(LogGroup.Engine, "Graph 路由指定了未知会话类型",
                new { channelId, sessionType, error = ex.Message });
            return false;
        }

        var activeId = GetFullClaimer(channelId);
        var activeSession = activeId == null ? null : GetSession(activeId);
        if (activeSession?.IsAlive == true)
        {
            messageEngine.DeliverToSession(channelId, message, isMentioned);
            Signal.Event(LogGroup.Engine, "Graph 路由到频道活跃会话",
                new { channelId, sessionId = activeSession.SessionId, sessionType = activeSession is RuntimeSession rs ? rs.Type : null });
            return true;
        }

        var spec = new CreateSessionSpec
        {
            Type = profile.Type,
            ChannelId = channelId,
            AutoClaimChannel = true,
            InputPorts = new Dictionary<string, PortSpec>(StringComparer.OrdinalIgnoreCase)
            {
                ["ch0:normal"] = new PortSpec { ChannelId = channelId, SubStream = "normal", Weight = 5, PreloadHistory = false },
                ["ch0:mention"] = new PortSpec { ChannelId = channelId, SubStream = "mention", Weight = 100, PreloadHistory = false },
            },
            OutputPorts = new Dictionary<string, string>(),
        };

        var created = CreateSession(spec);
        if (GetFullClaimer(channelId) != created.SessionId)
        {
            Signal.Warn(LogGroup.Engine, "Graph 路由创建会话未取得频道占用",
                new { channelId, sessionId = created.SessionId, sessionType });
            return false;
        }

        messageEngine.DeliverToSession(channelId, message, isMentioned);
        Signal.Event(LogGroup.Engine, "Graph 路由创建频道会话并投递消息",
            new { channelId, sessionId = created.SessionId, sessionType });
        return true;
    }

    public IRuntimeSession? CreateExpressSessionForChannel(int channelId)
    {
        if (channelId <= 0) return null;
        lock (_poolLock)
        {
            if (_pools.TryGetValue(channelId, out var pool) && pool.HasFullClaim)
                return null;
        }
        return CreateSession(new CreateSessionSpec
        {
            Type = "express",
            AutoClaimChannel = true,
            InputPorts = new Dictionary<string, PortSpec>(StringComparer.OrdinalIgnoreCase)
            {
                // express 全量占用：normal 攒权重 + mention 即时。端口即连接（AutoClaim 用首个 channelId）。
                ["ch0:normal"] = new PortSpec { ChannelId = channelId, SubStream = "normal", Weight = 5, PreloadHistory = false },
                ["ch0:mention"] = new PortSpec { ChannelId = channelId, SubStream = "mention", Weight = 100, PreloadHistory = false },
            },
            OutputPorts = new Dictionary<string, string>(),
            InitialState = null,
        });
    }

    /// <summary>
    /// express 会话 escalate → 同池换活跃 session 为 working 会话。claim 原地不动（不释放、不重取）。
    /// 镜像 CreateFromCompact：先 TakeOver 预定新 working id，CreateSession(fixedSessionId) 幂等承接。
    /// </summary>
    public async Task<IRuntimeSession> CreateFromEscalateAsync(RuntimeSession expressSession, string reason)
    {
        var newId = Guid.NewGuid().ToString("N")[..12];
        var channelId = expressSession.ConnectedChannel.CurrentChannelId;
        if (channelId <= 0)
            throw new InvalidOperationException($"express 会话 {expressSession.SessionId} escalate 时未连接频道");

        TakeOverChannelPool(channelId, newId);

        var spec = new CreateSessionSpec
        {
            Type = "channel",
            AutoClaimChannel = true,
            InputPorts = new Dictionary<string, PortSpec>(StringComparer.OrdinalIgnoreCase)
            {
                // working 全量占用：escalate 后同池已 TakeOver 到本 id，端口即连接（AutoClaim 幂等命中）
                ["ch0:normal"] = new PortSpec { ChannelId = channelId, SubStream = "normal", Weight = 5, PreloadHistory = false },
                ["ch0:mention"] = new PortSpec { ChannelId = channelId, SubStream = "mention", Weight = 100, PreloadHistory = false },
            },
            OutputPorts = new Dictionary<string, string>(),
            // 模式身份提示词由 channel.json PromptFiles 加载；此处只放实例特有声明的 escalate 原因
            InitialState = $"用户请求切换到工作模式，原因：{reason}",
            ParentSessionId = expressSession.SessionId,
        };

        var working = (RuntimeSession)CreateSession(spec, fixedSessionId: newId);

        Signal.Event(LogGroup.Engine, "express escalate → working（同池换活跃 session）",
            new { channelId, expressId = expressSession.SessionId, workingId = working.SessionId });
        return working;
    }

    public StopResult StopSession(string id, string reason)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return new StopResult { Success = false, BlockReason = "Not found" };

        var children = GetActiveChildren(id);
        if (children != null && children.Count > 0)
            return new StopResult { Success = false, BlockReason = "Has running children", BlockingChildren = children.ToList() };

        // 资源阻塞检查（下载/SSH in-flight）
        if (AnyResourceBlocksExit(id))
            return new StopResult { Success = false, BlockReason = "Has active resources" };

        session.RequestStop(reason);
        ReleaseSessionClaims(id);
        _sessionResources.NotifyFreedAsync(id).GetAwaiter().GetResult();
        return new StopResult { Success = true };
    }

    public StopResult ForceKill(string id)
    {
        foreach (var child in _sessions.Values.Where(s => s.ParentSessionId == id).ToList())
            ForceKill(child.SessionId);

        if (_sessions.TryGetValue(id, out var session))
            session.RequestStop("force kill");

        ReleaseSessionClaims(id);
        _ = _sessionResources.NotifyFreedAsync(id);
        return new StopResult { Success = true };
    }

    public IRuntimeSession? GetSession(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : null;

    /// <summary>会话所在的资源池（WebUI 详情/池树展示用）。无池返回 null。</summary>
    public SessionPool? GetPoolBySession(string sessionId) => FindPoolBySession(sessionId);

    /// <summary>全部资源池快照（WebUI 池树展示用；复制后调用方无需持锁）。</summary>
    public List<SessionPool> GetPoolsSnapshot()
    {
        lock (_poolLock)
        {
            var seen = new HashSet<SessionPool>();
            var result = new List<SessionPool>();
            void Add(SessionPool pool)
            {
                if (!seen.Add(pool)) return;
                result.Add(pool);
                foreach (var child in pool.ChildPools)
                    Add(child);
            }

            foreach (var pool in _pools.Values)
                Add(pool);
            return result;
        }
    }

    // ── 子链查询（资源池树）──

    /// <summary>某会话的活跃子 agent 列表。走池树：所在池的 ChildPools 的活跃 session。</summary>
    public IReadOnlyCollection<string>? GetActiveChildren(string sessionId)
    {
        lock (_poolLock)
        {
            if (!_poolByMember.TryGetValue(sessionId, out var pool)) return null;
            var children = pool.ChildPools
                .Where(cp => !string.IsNullOrEmpty(cp.ActiveSessionId))
                .Select(cp => cp.ActiveSessionId)
                .ToList();
            return children.Count > 0 ? children : null;
        }
    }

    /// <summary>
    /// 解析会话引用到当前活跃 session ID（走资源池树：引用 → 所在池 → 池活跃 session）。
    /// 输入为裸 sessionId（LoopId 前缀概念已移除）。查不到返回 null。
    /// </summary>
    public string? ResolveActiveSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        if (_poolByMember.TryGetValue(sessionId, out var pool) && !string.IsNullOrEmpty(pool.ActiveSessionId))
            return pool.ActiveSessionId;
        return null;
    }

    /// <summary>子 agent 兼容入口：返回 childId 的直接父 session ID。沿池树：子池 → 父池 → 父池活跃 session。</summary>
    public string? ResolveEffectiveParent(string childId)
    {
        if (_poolByMember.TryGetValue(childId, out var pool) && pool.Parent != null)
            return pool.Parent.ActiveSessionId;
        return null;
    }

    public void OnChildCompleted(string childId)
    {
        lock (_poolLock)
        {
            if (!_poolByMember.TryGetValue(childId, out var sub)) return;
            var parentPool = sub.Parent;
            if (parentPool != null)
            {
                parentPool.ChildPools.Remove(sub);
                var parentId = parentPool.ActiveSessionId;
                if (!string.IsNullOrEmpty(parentId))
                {
                    var parent = GetSession(parentId) as RuntimeSession;
                    parent?.RemoveChildFromState(childId);
                }
            }
            // 清理子池成员索引（子 agent 已结束）
            foreach (var m in sub.Members)
                _poolByMember.TryRemove(m, out _);
        }
    }

    public IReadOnlyList<IRuntimeSession> GetActiveSessions() =>
        _sessions.Values.Where(s => s.IsAlive).ToList();

    /// <summary>完成所有活跃会话并落盘（--test 退出清理用）：标记 Completed，释放频道占用，避免残留 Running 阻塞下次验证。单会话失败不阻塞。</summary>
    public async Task CompleteAllSessionsAsync(string reason)
    {
        foreach (var s in _sessions.Values.ToList())
        {
            try
            {
                s.MarkCompletedForCleanup();
                await s.PersistStateAsync();
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "会话退出清理失败",
                    new { sessionId = s.SessionId, reason, error = ex.Message });
            }
        }
    }

    /// <summary>对指定会话类型的所有运行中会话，翻转已装载组件实例的启用状态（最佳努力，失败跳过）。</summary>
    public async Task ToggleComponentForTypeAsync(string componentName, string type, bool enabled)
    {
        foreach (var s in _sessions.Values.Where(s => s.IsAlive
                     && string.Equals(s.Type, type, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (s is RuntimeSession rs)
                    await rs.ToggleComponentAsync(componentName, enabled);
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "运行中会话组件翻转失败",
                    new { sessionId = s.SessionId, component = componentName, error = ex.Message });
            }
        }
    }

    public bool HasActiveWorking(int channelId)
    {
        lock (_poolLock)
            return _pools.TryGetValue(channelId, out var pool) && pool.HasFullClaim;
    }

    // ── 频道占用（占用状态在资源池里）──

    /// <summary>池字典与池内状态的互斥锁（频道占用变更频率低，全局锁足够安全）。</summary>
    private readonly object _poolLock = new();

    /// <summary>供 RuntimeSession 连接动作查询频道名称。</summary>
    public Task<Database.Channel?> GetChannelByIdAsync(int channelId)
        => _ctx.Session.GetChannelByIdAsync(channelId);

    /// <summary>供 RuntimeSession 连接动作订阅频道消息子流。</summary>
    public IInputPort SubscribeChannel(int channelId, string subStream, int weight)
    {
        var me = _ctx.GetActiveEnginesSnapshot().OfType<Messaging.MessageEngine>().FirstOrDefault();
        return me!.Subscribe(channelId, subStream, weight);
    }

    /// <summary>供 RuntimeSession 断开动作退订频道端口。</summary>
    public void UnsubscribeChannel(IInputPort port)
    {
        var me = _ctx.GetActiveEnginesSnapshot().OfType<Messaging.MessageEngine>().FirstOrDefault();
        me?.Unsubscribe(port);
    }

    public bool ClaimChannel(string sessionId, int channelId, ClaimLevel level)
    {
        lock (_poolLock)
        {
            var pool = _pools.GetOrAdd(channelId, _ => new SessionPool(channelId));
            if (level == ClaimLevel.Full)
            {
                if (!pool.Claim(sessionId)) return false;
                _poolByMember[sessionId] = pool;
            }
            else
            {
                if (!pool.ListenSessionIds.Contains(sessionId))
                    pool.ListenSessionIds.Add(sessionId);
                _poolByMember[sessionId] = pool;
            }
            return true;
        }
    }

    /// <summary>
    /// 同池换活跃 session（escalate / compact 专用）：占用状态原地不动，不释放、不重取。
    /// express escalate 到 working 时两者共享同一池，claim 从 express 平滑移交 working。
    /// </summary>
    public bool TakeOverChannelPool(int channelId, string newSessionId)
    {
        lock (_poolLock)
        {
            if (!_pools.TryGetValue(channelId, out var pool) || !pool.HasFullClaim)
                return false;
            pool.TakeOver(newSessionId);
            _poolByMember[newSessionId] = pool;
            return true;
        }
    }

    public void ReleaseChannel(string sessionId, int channelId)
    {
        lock (_poolLock)
        {
            if (!_pools.TryGetValue(channelId, out var pool)) return;
            pool.Release(sessionId);
            pool.ListenSessionIds.RemoveAll(x => x == sessionId);
            RemoveEmptyPool(pool);
        }
    }

    public string? GetFullClaimer(int channelId)
    {
        lock (_poolLock)
            return _pools.TryGetValue(channelId, out var pool) && pool.HasFullClaim
                ? pool.ActiveSessionId : null;
    }

    public bool IsChannelClaimed(int channelId)
    {
        lock (_poolLock)
            return _pools.TryGetValue(channelId, out var pool) && pool.HasFullClaim;
    }

    /// <summary>单频道会话快照（WebUI 频道引擎监控改读会话）。</summary>
    public record ChannelSessionSnapshot(
        int ChannelId, string SessionId, string Type,
        bool IsAlive, DateTime LastActivity, int RoundCount);

    /// <summary>某频道的活跃会话快照（express/working）。无占用返回 null。</summary>
    public ChannelSessionSnapshot? GetChannelSessionSnapshot(int channelId)
    {
        lock (_poolLock)
        {
            if (!_pools.TryGetValue(channelId, out var pool) || !pool.HasFullClaim) return null;
            if (!_sessions.TryGetValue(pool.ActiveSessionId, out var s)) return null;
            return new ChannelSessionSnapshot(
                channelId, s.SessionId, s.Type, s.IsAlive,
                s.LastActivityInternal, (s as RuntimeSession)?.RoundCountInternal ?? 0);
        }
    }

    /// <summary>所有频道的活跃会话快照。</summary>
    public List<ChannelSessionSnapshot> GetChannelSessionSnapshots()
    {
        lock (_poolLock)
        {
            var list = new List<ChannelSessionSnapshot>();
            foreach (var pool in _pools.Values)
            {
                if (!pool.HasFullClaim) continue;
                if (!_sessions.TryGetValue(pool.ActiveSessionId, out var s)) continue;
                list.Add(new ChannelSessionSnapshot(
                    pool.ChannelId, s.SessionId, s.Type, s.IsAlive,
                    s.LastActivityInternal, (s as RuntimeSession)?.RoundCountInternal ?? 0));
            }
            return list;
        }
    }

    /// <summary>通过外部信号唤醒指定 session（异步任务完成等）。自动跨 compact forward。</summary>
    public void WakeUpSession(string sessionId)
    {
        var active = ResolveActiveSession(sessionId);
        if (active == null) return;
        if (_sessions.TryGetValue(active, out var session))
            session.WakeUp();
    }

    /// <summary>
    /// 外部切换 session 活跃 Core（命令 / WebUI 触发）。返回 (成功, 消息)。
    /// 跨 compact forward 解析到当前活跃 session，校验白名单后切换并唤醒。
    /// </summary>
    public (bool Success, string Message) SwitchSessionCore(string sessionId, string coreName)
    {
        var active = ResolveActiveSession(sessionId);
        if (active == null)
            return (false, $"会话 {sessionId} 不存在或已结束。");

        if (!_sessions.TryGetValue(active, out var session) || session is not RuntimeSession rs)
            return (false, $"会话 {active} 不可用。");

        if (!rs.AllowedCoreConfigs.Contains(coreName, StringComparer.OrdinalIgnoreCase))
            return (false, $"核心 {coreName} 不在会话 {active} 可用列表内（{string.Join(", ", rs.AllowedCoreConfigs)}）。");

        if (!rs.SwitchCore(coreName))
            return (false, $"切换核心失败：{coreName}。");

        rs.WakeUp();
        Signal.Event(LogGroup.Engine, "外部切换 Session Core",
            new { sessionId = active, core = rs.ActiveCoreConfig, trigger = "external" });
        return (true, $"会话 {active} 已切换到核心 {rs.ActiveCoreConfig}。");
    }

    // 注册表对外入口
    public void RegisterSessionResource(AgentLilara.PluginSDK.Services.ISessionResource r) => _sessionResources.Register(r);
    public void UnregisterSessionResource(AgentLilara.PluginSDK.Services.ISessionResource r) => _sessionResources.Unregister(r);

    // ISessionResourceAccess 显式实现
    void AgentLilara.PluginSDK.Services.ISessionResourceAccess.Register(AgentLilara.PluginSDK.Services.ISessionResource r) => _sessionResources.Register(r);
    void AgentLilara.PluginSDK.Services.ISessionResourceAccess.Unregister(AgentLilara.PluginSDK.Services.ISessionResource r) => _sessionResources.Unregister(r);

    internal System.Threading.Tasks.Task NotifyResourcesCompactingAsync(string oldId, string newId) => _sessionResources.NotifyCompactingAsync(oldId, newId);
    internal System.Threading.Tasks.Task NotifyResourcesFreedAsync(string id) => _sessionResources.NotifyFreedAsync(id);
    internal bool AnyResourceBlocksExit(string sessionId) => _sessionResources.AnyHasActiveResources(sessionId);

    internal void ReleaseSessionClaims(string sessionId)
    {
        lock (_poolLock)
        {
            if (!_poolByMember.TryGetValue(sessionId, out var pool)) return;
            if (pool.ActiveSessionId == sessionId)
            {
                pool.Release(sessionId);
                RemoveEmptyPool(pool);
            }
        }
    }

    /// <summary>空池回收：顶层池从 _pools 移除 / 子池从父移除，并清空其成员索引（成员已全部死亡）。</summary>
    private void RemoveEmptyPool(SessionPool pool)
    {
        if (!pool.IsEmpty) return;
        if (pool.IsRoot)
            _pools.TryRemove(pool.ChannelId, out _);
        else
            pool.Parent?.ChildPools.Remove(pool);
        foreach (var m in pool.Members)
            _poolByMember.TryRemove(m, out _);
    }

    /// <summary>
    /// express 会话空闲回收（30s 监控循环调用）。空闲超时 = ImpulseConfig.ColdTimeoutSeconds（默认 600s）。
    /// 只杀 Type="express" 会话，绝不误伤 working/task。回收后池释放 claim，频道回无占用。
    /// </summary>
    private void ReclaimIdleExpressSessions()
    {
        var idleThreshold = TimeSpan.FromSeconds(_ctx.ImpulseConfig.ColdTimeoutSeconds);
        var now = DateTime.UtcNow;
        List<RuntimeSession> idle;
        lock (_sessions)
        {
            idle = _sessions.Values
                .Where(s => s.Type == "express" && s.IsAlive)
                .Where(s => (now - s.LastActivityInternal) > idleThreshold)
                .ToList();
        }
        foreach (var rs in idle)
        {
            Signal.Event(LogGroup.Engine, "express 会话空闲回收",
                new { sessionId = rs.SessionId, idleSeconds = (int)(now - rs.LastActivityInternal).TotalSeconds });
            rs.RequestStop("express idle reclaim");
        }
    }

    // ── ScheduledTask ──

    public void AddScheduledTask(ScheduledTask task)
    {
        lock (_scheduledTasks) _scheduledTasks.Add(task);
    }

    public IReadOnlyList<ScheduledTask> GetScheduledTasks()
    {
        lock (_scheduledTasks) return _scheduledTasks.ToList();
    }

    // ── TODO ──

    public void AddTodo(TodoItem todo)
    {
        lock (_todoItems) _todoItems.Add(todo);
    }

    public IReadOnlyList<TodoItem> GetPendingTodos(int? relatedPersonId = null)
    {
        lock (_todoItems)
        {
            return _todoItems
                .Where(t => t.Status == "pending")
                .Where(t => relatedPersonId == null || t.RelatedPersonId == relatedPersonId)
                .Where(t => t.NotBefore <= DateTime.UtcNow)
                .Where(t => _currency == null || _currency.Balance >= t.MinBalance)
                .ToList();
        }
    }

    public void CompleteTodo(string todoId)
    {
        lock (_todoItems)
        {
            var todo = _todoItems.FirstOrDefault(t => t.TodoId == todoId);
            if (todo != null) todo.Status = "done";
        }
    }

    // ── 30s 监控循环 ──

    public async Task RunAsync()
    {
        // 引擎生命周期信号（Continue 从启动者 signal 建立因果连线，独立 signalId 不与启动者合并）
        var parentCtx = SignalContext.Current;
        var lifeCtx = Signal.Continue(
            SignalContext.NewSignalId(), parentCtx?.CurrentSpanId,
            "session-engine:main", LogGroup.Engine, "Session引擎",
            new { engineType = EngineType });

        // 恢复持久化的 sessions
        RestoreAll();

        try
        {
            while (IsAlive)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30));

                    // 1. express 会话空闲回收：空闲超时（ColdTimeoutSeconds）直接杀，释放 claim。
                    //    express 无长期状态（记忆只读+突发语义），回收即"频道回无占用"，下次消息重新孵化。
                    ReclaimIdleExpressSessions();

                    // 2. 检查 WaitUntil 超时 → SessionEngine 的注入由 RuntimeSession 内部处理
                    // Session 的 WaitForActivationAsync 会自行检测 WaitUntil 到期

                    // 2. 检查 ScheduledTask
                    List<ScheduledTask> due;
                    lock (_scheduledTasks)
                    {
                        due = _scheduledTasks
                            .Where(t => t.Status == "pending" && t.ExecuteAt <= DateTime.UtcNow)
                            .ToList();
                    }

                    foreach (var task in due)
                    {
                        task.Status = "executing";
                        CreateSession(new CreateSessionSpec
                        {
                            Type = "task",
InitialState = $"执行定时任务: {task.Description}"
                            });

                        // Recurring → 排下一次
                        if (task.Type == ScheduleType.Recurring && !string.IsNullOrEmpty(task.CronExpression))
                        {
                            task.ExecuteAt = CalculateNextCron(task.CronExpression, DateTime.UtcNow);
                            task.Status = "pending";
                        }
                    }

                    // 2.5 伴随提取空闲补一次（攒批有尾巴且已静默 → 提取掉，不漏尾）
                    if (Mind?.Extract?.Express != null)
                        await Mind.Extract.Express.TriggerIdleAsync();

                    // 3. 检查 TODO（每 15 分钟扫一次）
                    if ((DateTime.UtcNow - _lastTodoScan).TotalMinutes >= 15)
                    {
                        _lastTodoScan = DateTime.UtcNow;
                        var pending = GetPendingTodos()
                            .OrderBy(t => t.CreatedAt)
                            .Take(1) // 一次只处理一条
                            .ToList();

                        foreach (var todo in pending)
                        {
                            todo.Status = "executing";
                            CreateSession(new CreateSessionSpec
                            {
                                Type = "task",
                                InitialState = $"待办事项: {todo.Description}"
                            });
                        }
                    }
                    // 4. Review 会话按需孵化：队列有待处理项 + 无活跃 review 会话 → 创建。
                    //    review 会话一次处理一个队列项（认领→查证→暂存→提交→收尾），队列空则 end_session 退出。
                    if (Mind?.Queue != null)
                    {
                        bool hasActiveReview = _sessions.Values.Any(s => s.Type == "review" && s.IsAlive);
                        if (!hasActiveReview && await Mind.Queue.HasPendingAsync())
                        {
                            var stage = Mind.CreateReviewStage(Guid.NewGuid().ToString("N"));
                            var reviewSession = CreateSession(new CreateSessionSpec
                            {
                                Type = "review",
                                InitialState = "新的待验证队列项已就绪，开始处理。",
                                SessionServices = new Dictionary<System.Type, object>
                                {
                                    [typeof(AgentLilara.PluginSDK.Services.IReviewStageAccess)] =
                                        new Tool.Host.ReviewStageAccessImpl(stage)
                                }
                            });
                            Signal.Event(LogGroup.Engine, "Review 会话孵化",
                                new { sessionId = reviewSession.SessionId, queueHasPending = true });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Engine, "Session引擎监控循环异常", new { error = ex.Message });
                }
            }
        }
        finally
        {
            // 退出时全量持久化
            await PersistAllAsync();
            lifeCtx.Close(new { engineType = EngineType, reason = "shutdown" });
        }
    }

    public void OnEvent(EngineEvent e) { }
    public void RequestStop() => IsAlive = false;

    // ── 持久化 ──

    private void RestoreAll()
    {
        var baseDir = Path.Combine(PathConfig.StoragePath, "Sessions");
        if (!Directory.Exists(baseDir)) return;

        // ── 阶段 1：扫描所有 Running/Crashed 存档，按 (channelId) 分组取 createdAt 最新者为"幸存者"。
        //    防风暴 / 防 compact 链残留：同 channelId 多个 Running 存档时，旧的 compact 链条已被
        //    最新存档取代，老存档启动也没意义且会争抢 Full claim。无 ConnectedChannelId 的孤儿
        //    归入 key=0 一组。
        var loaded = new List<(string dir, SessionPersistence persister, CreateSessionSpec spec, SessionStateSnapshot state, List<Models.Message> history)>();
        var survivalWinners = new Dictionary<int, (DateTime createdAt, int index)>();

        foreach (var dir in Directory.GetDirectories(baseDir))
        {
            SessionPersistence persister;
            CreateSessionSpec spec;
            SessionStateSnapshot state;
            List<Models.Message> history;

            try
            {
                persister = new SessionPersistence(Path.GetFileName(dir));
                (spec, state, history) = persister.Load();
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "Session 存档读取失败（跳过）",
                    new { sessionId = Path.GetFileName(dir), error = ex.Message });
                continue;
            }

            // 冷启动过滤：只恢复 Running/Crashed，Completed/Creating 保留存档但不加载
            if (state.Status is not (SessionStatus.Running or SessionStatus.Crashed))
            {
                Signal.Debug(LogGroup.Engine, "Session 存档跳过（已结束）",
                    new { sessionId = state.SessionId, status = state.Status.ToString() });
                continue;
            }

            int key = 0;
            if (!string.IsNullOrEmpty(state.ConnectedChannelId)
                && int.TryParse(state.ConnectedChannelId, out var cid) && cid > 0)
                key = cid;

            int idx = loaded.Count;
            loaded.Add((dir, persister, spec, state, history));

            if (!survivalWinners.TryGetValue(key, out var cur))
                survivalWinners[key] = (state.CreatedAt, idx);
            else if (state.CreatedAt > cur.createdAt)
                survivalWinners[key] = (state.CreatedAt, idx);
        }

        var survivorIndexes = new HashSet<int>(
            survivalWinners.Values.Select(v => v.index));

        // 被淘汰者先标 Completed（避免下次重启又尝试），不启动不订阅
        for (int i = 0; i < loaded.Count; i++)
        {
            if (survivorIndexes.Contains(i)) continue;
            var (dir, persister, spec, state, history) = loaded[i];
            try
            {
                persister.UpdateStatusAsync(SessionStatus.Completed).GetAwaiter().GetResult();
                Signal.Event(LogGroup.Engine, "Session 存档被幸存者取代，标记为 Completed",
                    new { sessionId = state.SessionId, channelId = state.ConnectedChannelId, createdAt = state.CreatedAt });
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "Session 存档标 Completed 失败",
                    new { sessionId = state.SessionId, error = ex.Message });
            }
        }

        // ── 阶段 2：仅对幸存者走恢复流程 ──
        // 单个会话恢复失败（坏存档等）只跳过自己、不拖垮整个引擎：RestoreAll 完不成
        // 会连带 SessionEngine 启动失败，频道却已被占用 → 冷启动假占用吞消息（已知坑）。
        // 分两阶段：
        //   A. 先重建会话对象（组件/订阅/历史/登记/启动 RunAsync）——中途失败不产生任何 claim；
        //   B. 全部重建成功后，再统一 ClaimChannel。claim 只对"已完整重建"的会话生效，
        //      从根上杜绝"claim 已落地但会话对象不存在"的幽灵占用中间态。
        var restoredWait = new List<(string sessionId, DateTime waitUntil)>();
        // 需要恢复 Full claim 的会话：(sessionId, channelId)。与旧实现一致：claim 用
        // restorePorts 首个端口频道（spec.InputPorts 非空 或 从 state.ConnectedChannelId 推断 两条路都进）。
        var restoreClaims = new List<(string sessionId, int channelId)>();
        foreach (var idx in survivorIndexes)
        {
            var (dir, persister, spec, state, history) = loaded[idx];

            // ① 收集需要恢复的频道端口。
            //    spec.InputPorts 非空 → 直接用（理论路径）。
            //    spec.InputPorts 空但 state.ConnectedChannelId 非空 → escalate 路径，端口由旧 session
            //    执行 InitActions.connect_channel 动态创建，spec 持久化时未保留端口信息，这里从 state 推断。
            //    ConnectionMode.Full → normal + mention 两端口；Listen → 仅 normal。
            var restorePorts = new List<(string name, PortSpec spec)>();
            var restoreMode = ConnectionMode.None;
            if (spec.InputPorts.Count > 0)
            {
                foreach (var (name, ps) in spec.InputPorts)
                    restorePorts.Add((name, ps));
                if (!string.IsNullOrEmpty(state.ConnectedMode)
                    && Enum.TryParse(state.ConnectedMode, true, out restoreMode) && restoreMode == ConnectionMode.None)
                    restoreMode = ConnectionMode.Full;
            }
            else if (!string.IsNullOrEmpty(state.ConnectedChannelId)
                     && int.TryParse(state.ConnectedChannelId, out var restoredCid) && restoredCid > 0)
            {
                restoreMode = Enum.TryParse(state.ConnectedMode, true, out restoreMode)
                    ? restoreMode : ConnectionMode.Full;
                restorePorts.Add(("default", new PortSpec { ChannelId = restoredCid, SubStream = "normal", Weight = 5, PreloadHistory = false }));
                if (restoreMode == ConnectionMode.Full)
                    restorePorts.Add(("mention", new PortSpec { ChannelId = restoredCid, SubStream = "mention", Weight = 100, PreloadHistory = false }));
                Signal.Event(LogGroup.Engine, "从 state 推断恢复端口",
                    new { sessionId = state.SessionId, channelId = restoredCid, mode = restoreMode.ToString() });
            }

            RuntimeSession session;
            try
            {
                // ② 重建 RuntimeSession（复用旧 SessionId）。finalComponents 必填——组件装配
                //    与 CreateSession 用同一 ComputeFinalComponents，保证恢复=新建。
                var profile = SessionTypeProfiles.Get(spec.Type);
                session = new RuntimeSession(spec, state.SessionId, ComputeFinalComponents(spec, profile));
                session.SetCurrency(_currency!);
                session.SetSessionEngine(this);
                session.OnCompleted += OnRuntimeSessionCompleted;

                // 复活善后：订阅重建 session 的完成事件
                if (Mind?.Resurrector != null)
                    Mind.Resurrector.Attach(session);

                // ③ 走标准初始化路径（组件 / AgentCore / MessageEngine / AgentHost）
                //    跳过 BootstrapAsync：InitActions 已在旧 session 执行过，不重复 claim/订阅。
                //    会话身份统一为裸 sessionId（LoopId 概念已移除）。
                try
                {
                    session.InitComponents(session.SessionId, _ctx.ModuleBus, _ctx.ComponentServices, () => session.WakeUp());
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Engine, "恢复 Session 组件初始化失败",
                        new { sessionId = session.SessionId, error = ex.Message });
                }

                _sessions[session.SessionId] = session;

                session.ConfigureCores(profile.AllowedCoreConfigs, profile.DefaultCoreConfig, state.ActiveCoreConfig);
                var messageEngine = _ctx.GetActiveEnginesSnapshot().OfType<Messaging.MessageEngine>().FirstOrDefault();
                if (messageEngine != null) session.SetMessageEngine(messageEngine);

                // ④ 重建输入端口 + 订阅（不重新订阅会丢消息）
                foreach (var (portName, portSpec) in restorePorts)
                {
                    if (messageEngine != null)
                    {
                        var port = messageEngine.Subscribe(portSpec.ChannelId, portSpec.SubStream, portSpec.Weight);
                        session.AddInputPort(portName, port);
                    }
                }

                // 恢复的 session 用原 SessionId 重新绑定自己的 session-scope 图实例。
                InitializeGraphMount(session);

                // ⑤ 重建 SessionAgentHost
                try
                {
                    var channelPorts = new List<(int channelId, string? channelName, bool preload)>();
                    foreach (var (_, portSpec) in restorePorts)
                    {
                        var ch = _ctx.Session.GetChannelByIdAsync(portSpec.ChannelId).GetAwaiter().GetResult();
                        channelPorts.Add((portSpec.ChannelId, ch?.Name, portSpec.PreloadHistory));
                    }

                    var dataSource = new SystemContextDataSource(_ctx);
                    var svc = new ChannelContextService(dataSource);
                    var initialStateText = spec.InitialState is string s ? s
                        : spec.InitialState != null ? JsonConvert.SerializeObject(spec.InitialState)
                        : null;
                    var host = new SessionAgentHost(channelPorts, initialStateText, svc);
                    session.SetAgentHost(host);
                }
                catch (Exception ex)
                {
                    Signal.Error(LogGroup.Engine, "恢复 Session AgentHost 失败",
                        new { sessionId = session.SessionId, error = ex.Message });
                }

                // ⑥ 灌入存档状态 + 历史
                session.RestoreFromSnapshot(state, history);

                // ⑦ 注入 persister + 启用压缩
                session.SetPersister(persister);
                session.InitCompression();

                Signal.Event(LogGroup.Engine, "Session 已恢复",
                    new { sessionId = session.SessionId, historyCount = history.Count, roundCount = state.RoundCount });

                // ⑧ 启动 RunAsync（restored 会话不立即激活，等新消息 / WaitUntil 到期 / 外部唤醒）
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var cts = new CancellationTokenSource();
                        await session.RunAsync(cts.Token);
                    }
                    catch (Exception ex)
                    {
                        Signal.Error(LogGroup.Engine, "恢复后 Session 崩溃",
                            new { sessionId = session.SessionId, error = ex.Message });
                    }
                    finally
                    {
                        ReleaseSessionClaims(session.SessionId);
                    }
                });

                // ⑨ 重调度 WaitUntil：恢复的 waitUntil 是旧的墙上时钟（可能已超时很久），
                //    且 RestoreFromSnapshot 已直接把 WaitUntil 灌进会话——若超时立即触发一轮
                //    wait timeout 注入（可接受）。但若 waitUntil 在未来很远（重启发生在 wait 期间），
                //    RunAsync 的 WaitForActivationAsync 轮询会报 TimeOutException（Task.Delay 超长溢出），
                //    把会话打 Crashed。这里对未来 wait 统一转成"等待到期时长"，到期后注入 timeout 唤醒。
                if (state.WaitUntil != null && state.WaitUntil > DateTime.UtcNow)
                {
                    restoredWait.Add((session.SessionId, state.WaitUntil.Value));
                }

                // 记录待恢复的 Full claim（供阶段 3 统一落地）：仅 restoreMode==Full 且端口非空。
                // escalate 路径（spec.InputPorts 空、从 state 推断端口）也在此覆盖，与旧实现对齐。
                if (restoreMode == ConnectionMode.Full && restorePorts.Count > 0)
                    restoreClaims.Add((session.SessionId, restorePorts[0].spec.ChannelId));
            }
            catch (Exception ex)
            {
                // 单个会话恢复失败：标 Completed 防下次重启再试，跳过（不产生 claim）。
                Signal.Error(LogGroup.Engine, "Session 恢复失败（跳过）",
                    new { sessionId = state.SessionId, error = ex.Message, stack = ex.StackTrace });
                try { persister.UpdateStatusAsync(SessionStatus.Completed).GetAwaiter().GetResult(); }
                catch { }
                continue;
            }
        }

        // ── 阶段 3：全部会话重建成功后统一恢复频道占用（先建后 claim，杜绝幽灵占用）──
        foreach (var (sid, cid) in restoreClaims)
        {
            // 会话在阶段 2 中恢复失败：未登记到 _sessions 则不建立 claim，原频道回无占用。
            if (!_sessions.ContainsKey(sid)) continue;
            var claimed = ClaimChannel(sid, cid, ClaimLevel.Full);
            Signal.Event(LogGroup.Engine, "恢复 Session 频道占用",
                new { sessionId = sid, channelId = cid, claimed });
        }

        // WaitUntil 到期重调度：用新会话唤醒回调投递 timeout（不注入消息历史，只触发激活判定）
        foreach (var (sid, waitUntil) in restoredWait)
        {
            if (!_sessions.TryGetValue(sid, out var rs)) continue;
            var delay = waitUntil - DateTime.UtcNow;
            if (delay <= TimeSpan.Zero) continue;
            var delayCap = TimeSpan.FromDays(100);
            var t = delay > delayCap ? delayCap : delay;
            _ = Task.Delay(t).ContinueWith(_ =>
            {
                if (_sessions.TryGetValue(sid, out var s)
                    && s is RuntimeSession r && r.WaitUntil == waitUntil)
                    r.InjectWaitTimeoutExpired();
            });
        }

        // 第二遍：重建子 agent 的池树。父 session 可能晚于子 session 恢复，必须等全部 session 就位后
        // 才能把子 agent 挂到父池（否则父池不存在，子池建不出）。
        foreach (var (_, session) in _sessions)
        {
            var specParent = session.ParentSessionId;
            if (!string.IsNullOrEmpty(specParent) && !_poolByMember.ContainsKey(session.SessionId))
            {
                // 优先用 effectiveParentId（compact 后的当前父）；无则用 spec.ParentSessionId（原始父）
                var parentId = session.EffectiveParentId ?? specParent;
                CreateSubPool(session.SessionId, parentId);
            }
        }

        // RestoreAll 完成：放行消息路由中等待的 claim 检查
        _restoreTcs.TrySetResult();
    }

    private async Task PersistAllAsync()
    {
        foreach (var (_, session) in _sessions)
        {
            try { await session.PersistStateAsync(); }
            catch { /* 持久化失败不阻塞 */ }
        }
    }

    private DateTime CalculateNextCron(string cronExpression, DateTime from)
    {
        // 简化 cron 计算：目前只支持 Minutes（如 "*/30 * * * *"）
        // 生产环境可引入 Cronos 库
        try
        {
            var parts = cronExpression.Split(' ');
            if (parts.Length >= 2 && parts[0].StartsWith("*/"))
            {
                var minutes = int.Parse(parts[0].Substring(2));
                return from.AddMinutes(minutes);
            }
        }
        catch { }
        return from.AddHours(1); // fallback
    }
}

/// <summary>
/// Session 停止结果。
/// </summary>
public class StopResult
{
    public bool Success { get; set; }
    public string? BlockReason { get; set; }
    public List<string>? BlockingChildren { get; set; }
}
