// AgentCoreProcessor/Engine/Session/SessionEngine.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine.Messaging;
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

        // 加载组件
        try
        {
            session.InitComponents(
                "sub-agent", // 复用现有 LoopApplicability
                _ctx.ModuleBus,
                _ctx.ComponentServices,
                () => { /* wake loop callback */ });
        }
        catch (Exception ex)
        {
            // 组件加载失败不阻塞 Session 创建
            Console.Error.WriteLine($"[SessionEngine] Component init failed for {session.SessionId}: {ex.Message}");
        }

        _sessions[session.SessionId] = session;

        // 自动 ClaimChannel
        if (spec.AutoClaimChannel && spec.InputPorts.Count > 0)
        {
            var firstChannel = spec.InputPorts.First().Value.ChannelId;
            ClaimChannel(session.SessionId, firstChannel, ClaimLevel.Full);
        }

        // 启动 Session
        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource();
                await session.RunAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionEngine] Session {session.SessionId} crashed: {ex.Message}");
            }
            finally
            {
                // Session 结束时释放占用
                ReleaseSessionClaims(session.SessionId);
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
        // 恢复持久化的 sessions
        RestoreAll();

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
                Console.Error.WriteLine($"[SessionEngine] Monitor loop error: {ex.Message}");
            }
        }

        // 退出时全量持久化
        await PersistAllAsync();
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
                    Console.WriteLine($"[SessionEngine] Restoring session {sessionId}...");
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
