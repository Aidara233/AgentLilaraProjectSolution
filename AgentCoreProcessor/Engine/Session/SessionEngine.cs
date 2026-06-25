// AgentCoreProcessor/Engine/Session/SessionEngine.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
internal class SessionEngine : ISubEngine
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

    public SessionEngine(ISystemContext ctx)
    {
        _ctx = ctx;
    }

    public void SetCurrency(ICurrencyService currency) => _currency = currency;

    // ── Session CRUD ──

    public IRuntimeSession CreateSession(CreateSessionSpec spec)
    {
        var session = new RuntimeSession(spec);
        session.SetCurrency(_currency!);

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
                () => { /* wake loop callback */ });
        }
        catch (Exception ex)
        {
            // 组件加载失败不阻塞 Session 创建
            Signal.Error(LogGroup.Engine, "Session 组件初始化失败", new { sessionId = session.SessionId, error = ex.Message });
        }

        _sessions[session.SessionId] = session;

        // 注入 AgentCore（使用 spec 指定的 Core 配置名）和 MessageEngine
        var agentCore = new Core.AgentCore(spec.CoreConfigName);
        var messageEngine = _ctx.GetActiveEnginesSnapshot()
            .OfType<Messaging.MessageEngine>().FirstOrDefault();
        session.SetAgentCore(agentCore);
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

        // 构建 SessionAgentHost（异步查询 channelName 后注入）
        _ = Task.Run(async () =>
        {
            try
            {
                var channelPorts = new List<(int channelId, string? channelName, bool preload)>();
                foreach (var (_, portSpec) in spec.InputPorts)
                {
                    var ch = await _ctx.Session.GetChannelByIdAsync(portSpec.ChannelId);
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
        });

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

        return session;
    }

    public StopResult StopSession(string id, string reason)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return new StopResult { Success = false, BlockReason = "Not found" };

        var children = _sessions.Values
            .Where(s => s.ParentSessionId == id && s.IsAlive)
            .Select(s => s.SessionId)
            .ToList();

        if (children.Count > 0)
            return new StopResult { Success = false, BlockReason = "Has running children", BlockingChildren = children };

        session.RequestStop(reason);
        ReleaseSessionClaims(id);
        return new StopResult { Success = true };
    }

    public StopResult ForceKill(string id)
    {
        foreach (var child in _sessions.Values.Where(s => s.ParentSessionId == id).ToList())
            ForceKill(child.SessionId);

        if (_sessions.TryGetValue(id, out var session))
            session.RequestStop("force kill");

        ReleaseSessionClaims(id);
        return new StopResult { Success = true };
    }

    public IRuntimeSession? GetSession(string id) =>
        _sessions.TryGetValue(id, out var s) ? s : null;

    public IReadOnlyList<IRuntimeSession> GetActiveSessions() =>
        _sessions.Values.Where(s => s.IsAlive).ToList();

    public bool HasActiveWorking(int channelId)
    {
        lock (_channelFullClaims)
            return _channelFullClaims.ContainsKey(channelId);
    }

    // ── 频道占用 ──

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
                            ComponentIds = new[] { "basic-tools", "file-tools", "memory-tools" },
                            InitialState = $"执行定时任务: {task.Description}",
                            OverrideLimits = new SessionLimits { MaxLifetime = TimeSpan.FromMinutes(30), IdleTimeout = TimeSpan.FromMinutes(5) }
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
                                ComponentIds = new[] { "basic-tools", "file-tools", "memory-tools" },
                                InitialState = $"待办事项: {todo.Description}",
                                OverrideLimits = new SessionLimits { MaxLifetime = TimeSpan.FromMinutes(30), IdleTimeout = TimeSpan.FromMinutes(5) }
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

        foreach (var dir in Directory.GetDirectories(baseDir))
        {
            var metaPath = Path.Combine(dir, "meta.json");
            if (!File.Exists(metaPath)) continue;

            try
            {
                var json = File.ReadAllText(metaPath);
                var meta = JsonConvert.DeserializeAnonymousType(json, new { status = "" });
                if (meta?.status is "Running" or "Crashed")
                {
                    // 恢复 session（简化：仅状态为 Running/Crashed 的恢复）
                    var sessionId = Path.GetFileName(dir);
                    Signal.Event(LogGroup.Engine, "恢复 Session", new { sessionId });
                }
            }
            catch { /* skip corrupt */ }
        }
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
