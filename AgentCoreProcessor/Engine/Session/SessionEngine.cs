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
using AgentCoreProcessor.Engine.Context;
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
    private readonly Dictionary<int, string> _channelFullClaims = new();
    private readonly List<(int channelId, string sessionId)> _channelListens = new();
    private readonly List<ScheduledTask> _scheduledTasks = new();
    private readonly List<TodoItem> _todoItems = new();
    private ICurrencyService? _currency;
    private DateTime _lastTodoScan = DateTime.MinValue;

    // 父子链条
    private readonly ConcurrentDictionary<string, HashSet<string>> _activeChildren = new();
    private readonly ConcurrentDictionary<string, string> _childToParent = new();
    private readonly ConcurrentDictionary<string, string> _forwards = new();
    private readonly object _childLinkLock = new();

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

    public void SetCurrency(ICurrencyService currency) => _currency = currency;

    // ── Session CRUD ──

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
            var cfg = ComponentConfig.Load();
            finalComponents = set
                .Where(c => cfg.IsEnabled(c, profile.PermissionGroup, defaultEnabled: true))
                .ToArray();
        }

        var session = new RuntimeSession(spec, finalComponents);
        session.SetCurrency(_currency!);
        session.SetSessionEngine(this);

        // 确定 loopId：单频道端口时用 LoopId.ForChannel，让 BasicTools 能解析真实 channelId
        string loopId;
        int singleChannelId = -1;
        if (spec.InputPorts.Count == 1)
        {
            singleChannelId = spec.InputPorts.First().Value.ChannelId;
            loopId = LoopId.ForChannel(singleChannelId);
        }
        else
        {
            loopId = $"session:{session.SessionId}";
        }

        // 加载组件
        try
        {
            session.InitComponents(
                loopId, // 单频道端口时传 "channel:N"，让 BasicToolsComponent.ParseChannelId 能解析
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
                new { sessionId = session.SessionId, channelPorts = channelPorts.Count, loopId });
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

        return session;
    }

    /// <summary>
    /// compact 工具专属：旧 session 调 compact(summary) 触发新会话承接。
    /// 新 SessionId（新 GUID），ParentSessionId = 旧 sessionId，
    /// InitialState = "[上一会话摘要]\n{summary}\n\n请继续工作"，轮次接续旧 session。
    /// 同步生效：ClaimChannel 在 CreateSession 内同步完成，新 session 立马接管频道。
    /// </summary>
    public async Task<IRuntimeSession> CreateFromCompact(RuntimeSession oldSession, string summary)
    {
        // 先释放旧 session 的频道 claim，让新 session 能成功 ClaimChannel。
        // 旧 session 即将由 compact 工具触发 EndSession 退出 RunAsync，不再需要 claim；
        // 旧 session 退出时 Task.Run 包装层 finally 会再次 ReleaseSessionClaims（幂等）。
        // 不释放的话：CreateSession 内 ClaimChannel 会撞上旧 claim → 返回 false →
        // 新 session 没有 Full claim → 入向消息落回 ExpressEngine Express、
        // speak 与 Express 抢话、整体路由错乱。
        ReleaseSessionClaims(oldSession.SessionId);

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

        var newSession = (RuntimeSession)CreateSession(oldSpec);

        // 承接旧 session 的活跃核（同类型白名单必然包含）
        newSession.SetActiveCore(oldSession.ActiveCoreConfig);

        // 接续旧 session 的轮次计数，避免新 session 从 0 重新跑 free round
        newSession.InheritRoundCount(oldSession.GetRoundCountForCompact());

        // 子链迁移：compact → 所有子 session 的 parent 从旧 session 指向新 session
        lock (_childLinkLock)
        {
            if (_activeChildren.TryRemove(oldSession.SessionId, out var childrenOfOld))
            {
                _activeChildren[newSession.SessionId] = childrenOfOld;
                foreach (var childId in childrenOfOld)
                {
                    if (_childToParent.ContainsKey(childId))
                        _childToParent[childId] = newSession.SessionId;
                    try
                    {
                        var childPersister = new SessionPersistence(childId);
                        childPersister.UpdateEffectiveParentAsync(childId, newSession.SessionId).GetAwaiter().GetResult();
                    }
                    catch { }
                }
            }
            _forwards[oldSession.SessionId] = newSession.SessionId;
        }

        // 通知所有 ISessionResource：loopId 从旧 session rebind 到新 session
        await _sessionResources.NotifyCompactingAsync(oldSession.SessionId, newSession.SessionId);

        Signal.Event(LogGroup.Engine, "compact 创建新 session",
            new { oldId = oldSession.SessionId, newId = newSession.SessionId, summaryLen = summary.Length });

        return newSession;
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

    // ── 父子链查询 ──

    public IReadOnlyCollection<string>? GetActiveChildren(string sessionId)
    {
        return _activeChildren.TryGetValue(sessionId, out var s) ? s : null;
    }

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

    public void OnChildCompleted(string childId)
    {
        lock (_childLinkLock)
        {
            if (_childToParent.TryRemove(childId, out var parentId))
            {
                if (_activeChildren.TryGetValue(parentId, out var siblings))
                {
                    siblings.Remove(childId);
                    if (siblings.Count == 0) _activeChildren.TryRemove(parentId, out _);
                }
                var parent = GetSession(parentId) as RuntimeSession;
                parent?.RemoveChildFromState(childId);
            }
        }
    }

    public IReadOnlyList<IRuntimeSession> GetActiveSessions() =>
        _sessions.Values.Where(s => s.IsAlive).ToList();

    public bool HasActiveWorking(int channelId)
    {
        lock (_channelFullClaims)
            return _channelFullClaims.ContainsKey(channelId);
    }

    // ── 频道占用 ──

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
        if (level == ClaimLevel.Full)
        {
            lock (_channelFullClaims)
            {
                if (_channelFullClaims.ContainsKey(channelId))
                    return false;
                _channelFullClaims[channelId] = sessionId;
            }
        }
        else
        {
            lock (_channelListens)
                _channelListens.Add((channelId, sessionId));
        }
        return true;
    }

    public void ReleaseChannel(string sessionId, int channelId)
    {
        lock (_channelFullClaims)
        {
            if (_channelFullClaims.TryGetValue(channelId, out var holder) && holder == sessionId)
                _channelFullClaims.Remove(channelId);
        }
        lock (_channelListens)
            _channelListens.RemoveAll(x => x.channelId == channelId && x.sessionId == sessionId);
    }

    public string? GetFullClaimer(int channelId)
    {
        lock (_channelFullClaims)
            return _channelFullClaims.TryGetValue(channelId, out var s) ? s : null;
    }

    public bool IsChannelClaimed(int channelId)
    {
        lock (_channelFullClaims)
            return _channelFullClaims.ContainsKey(channelId);
    }

    /// <summary>通过外部信号唤醒指定 session（异步任务完成等）。自动跨 compact forward。</summary>
    public void WakeUpSession(string sessionId)
    {
        var active = ResolveActiveSession($"session:{sessionId}");
        if (active == null) return;
        var bareId = active.Substring("session:".Length);
        if (_sessions.TryGetValue(bareId, out var session))
            session.WakeUp();
    }

    /// <summary>
    /// 外部切换 session 活跃 Core（命令 / WebUI 触发）。返回 (成功, 消息)。
    /// 跨 compact forward 解析到当前活跃 session，校验白名单后切换并唤醒。
    /// </summary>
    public (bool Success, string Message) SwitchSessionCore(string sessionId, string coreName)
    {
        var active = ResolveActiveSession($"session:{sessionId}");
        if (active == null)
            return (false, $"会话 {sessionId} 不存在或已结束。");

        var bareId = active.Substring("session:".Length);
        if (!_sessions.TryGetValue(bareId, out var session) || session is not RuntimeSession rs)
            return (false, $"会话 {bareId} 不可用。");

        if (!rs.AllowedCoreConfigs.Contains(coreName, StringComparer.OrdinalIgnoreCase))
            return (false, $"核心 {coreName} 不在会话 {bareId} 可用列表内（{string.Join(", ", rs.AllowedCoreConfigs)}）。");

        if (!rs.SwitchCore(coreName))
            return (false, $"切换核心失败：{coreName}。");

        rs.WakeUp();
        Signal.Event(LogGroup.Engine, "外部切换 Session Core",
            new { sessionId = bareId, core = rs.ActiveCoreConfig, trigger = "external" });
        return (true, $"会话 {bareId} 已切换到核心 {rs.ActiveCoreConfig}。");
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

    private void ReleaseSessionClaims(string sessionId)
    {
        lock (_channelFullClaims)
        {
            var keys = _channelFullClaims.Where(kv => kv.Value == sessionId).Select(kv => kv.Key).ToList();
            foreach (var key in keys)
                _channelFullClaims.Remove(key);
        }
        lock (_channelListens)
            _channelListens.RemoveAll(x => x.sessionId == sessionId);
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
        // 引擎生命周期信号（Continue 从启动者 signal 建立因果连线，与其他引擎对齐）
        var parentCtx = SignalContext.Current;
        var lifeCtx = Signal.Continue(
            parentCtx?.SignalId ?? Signal.NewId(), parentCtx?.CurrentSpanId,
            "session-engine", LogGroup.Engine, "Session引擎",
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

                    // 1. 检查 WaitUntil 超时 → SessionEngine 的注入由 RuntimeSession 内部处理
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

        // ── 阶段 2：仅对幸存者走恢复流程
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

            // ② 先恢复频道占用（同步生效，挡住 ChannelEngine），抢在适配器投递消息之前
            if (restorePorts.Count > 0 && restoreMode == ConnectionMode.Full)
            {
                var cid = restorePorts[0].spec.ChannelId;
                var claimed = ClaimChannel(state.SessionId, cid, ClaimLevel.Full);
                Signal.Event(LogGroup.Engine, "恢复 Session 频道占用",
                    new { sessionId = state.SessionId, channelId = cid, claimed });
            }

            // ③ 重建 RuntimeSession（复用旧 SessionId）
            var session = new RuntimeSession(spec, state.SessionId);
            session.SetCurrency(_currency!);
            session.SetSessionEngine(this);

            // ④ 走标准初始化路径（组件 / AgentCore / MessageEngine / AgentHost）
            //    跳过 BootstrapAsync：InitActions 已在旧 session 执行过，不重复 claim/订阅。
            //    loopId 凭 restorePorts 推断：单频道端口 → "channel:N"，否则 → "session:{id}"。
            string loopId;
            if (restorePorts.Count == 1)
                loopId = LoopId.ForChannel(restorePorts[0].spec.ChannelId);
            else
                loopId = $"session:{session.SessionId}";

            try
            {
                session.InitComponents(loopId, _ctx.ModuleBus, _ctx.ComponentServices, () => session.WakeUp());
            }
            catch (Exception ex)
            {
                Signal.Error(LogGroup.Engine, "恢复 Session 组件初始化失败",
                    new { sessionId = session.SessionId, error = ex.Message });
            }

            _sessions[session.SessionId] = session;

            var profile = SessionTypeProfiles.Get(spec.Type);
            session.ConfigureCores(profile.AllowedCoreConfigs, profile.DefaultCoreConfig, state.ActiveCoreConfig);
            var messageEngine = _ctx.GetActiveEnginesSnapshot().OfType<Messaging.MessageEngine>().FirstOrDefault();
            if (messageEngine != null) session.SetMessageEngine(messageEngine);

            // ⑤ 重建输入端口 + 订阅（不重新订阅会丢消息）
            foreach (var (portName, portSpec) in restorePorts)
            {
                if (messageEngine != null)
                {
                    var port = messageEngine.Subscribe(portSpec.ChannelId, portSpec.SubStream, portSpec.Weight);
                    session.AddInputPort(portName, port);
                }
            }

            // ⑥ 重建 SessionAgentHost
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

            // ④ 灌入存档状态 + 历史
            session.RestoreFromSnapshot(state, history);

            // ⑤ 注入 persister + 启用压缩
            session.SetPersister(persister);
            session.InitCompression();

            Signal.Event(LogGroup.Engine, "Session 已恢复",
                new { sessionId = session.SessionId, historyCount = history.Count, roundCount = state.RoundCount });

            // ⑥ 启动 RunAsync
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
