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
internal partial class SessionEngine : ISubEngine, AgentLilara.PluginSDK.Services.ISessionResourceAccess, IChannelSessionQuery
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
        session.SetGraphRunnerProvider(() =>
            _ctx.Graphs?.RunnersForSession(session.SessionId, session.Type) ?? Array.Empty<KeywordGraphRunner>());

    /// <summary>心智引擎（统一写入口 + 情绪 + 可信度服务）。</summary>
    public Mind.MindEngine? Mind => _ctx.Mind;

    public void SetCurrency(ICurrencyService currency) => _currency = currency;

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

    /// <summary>
    /// 把一个外部事件的内容注入频道会话（订阅命中 / 跨会话唤醒）。
    /// <para>频道有活会话 → 注入它的待处理上下文并唤醒；没有 → 先建一个 express 会话再注入。
    /// **不写聊天消息表**：外部事件不是聊天消息，不该变成一条"用户说过的话"。</para>
    /// <para>返回 null 表示已注入，否则是失败原因。</para>
    /// </summary>
    internal string? InjectToChannelSession(int channelId, string content)
    {
        if (channelId <= 0) return "invalid-channel";
        if (string.IsNullOrWhiteSpace(content)) return "empty-content";

        var activeId = GetFullClaimer(channelId);
        var active = activeId is null ? null : GetSession(activeId);

        if (active is RuntimeSession live && live.IsAlive)
        {
            live.InjectExternal(content);
            Signal.Event(LogGroup.Engine, "graph.subscription.injected",
                new { channelId, sessionId = live.SessionId, reused = true, length = content.Length });
            return null;
        }

        var created = CreateExpressSessionForChannel(channelId);
        if (created is not RuntimeSession fresh)
        {
            Signal.Warn(LogGroup.Engine, "graph.subscription.session_create_failed",
                new { channelId, reason = created is null ? "channel-occupied-or-failed" : "unexpected-session-type" });
            return "session-create-failed";
        }

        fresh.InjectExternal(content);
        Signal.Event(LogGroup.Engine, "graph.subscription.injected",
            new { channelId, sessionId = fresh.SessionId, reused = false, length = content.Length });
        return null;
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

    public IReadOnlyList<IRuntimeSession> GetActiveSessions() =>
        _sessions.Values.Where(s => s.IsAlive).ToList();

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

    /// <summary>池字典与池内状态的互斥锁（频道占用变更频率低，全局锁足够安全）。</summary>
    private readonly object _poolLock = new();

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
