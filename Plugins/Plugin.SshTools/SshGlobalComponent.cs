// Plugins/Plugin.SshTools/SshGlobalComponent.cs
using System.Collections.Concurrent;
using Renci.SshNet;
using AgentLilara.PluginSDK;

namespace Plugin.SshTools;

[Component(Name = "ssh-tools-global", Scope = ComponentScope.Global)]
public class SshGlobalComponent : GlobalComponentBase
{
    private IGlobalComponentContext _ctx = null!;
    private SshClient? _client;
    private SshConfig _config = null!;
    private string _configDir = "";
    private Timer? _idleTimer;
    private readonly object _lock = new();

    private readonly ConcurrentDictionary<string, SshTask> _runningTasks = new();
    private readonly ConcurrentDictionary<string, SshTask> _completedTasks = new();

    /// <summary>异步任务完成回调，由 LoopComponent 注册以唤醒 loop。</summary>
    public Action<string>? OnTaskCompleted { get; set; }

    public SshClient? Client => _client;
    public SshConfig Config => _config;
    public string ConfigDir => _configDir;

    public override ComponentMeta Meta => new()
    {
        Name = "ssh-tools-global",
        Description = "SSH 连接管理：SshClient 单例 + 异步任务注册",
        DefaultEnabled = true,
        PromptPriority = 99
    };

    public override IEnumerable<ITool> Tools => Array.Empty<ITool>();

    public override Task OnInitAsync(IGlobalComponentContext context, InitReason reason)
    {
        _ctx = context;

        // 注册唤醒回调：任务完成 → WakeLoop
        OnTaskCompleted = loopId =>
        {
            _ctx.WakeLoop(loopId);
        };

        _configDir = Path.GetFullPath(Path.Combine(context.Storage.GlobalDirectory, ".."));
        _config = SshConfig.Load(_configDir);

        if (!string.IsNullOrEmpty(_config.Host))
            Connect();

        SshToolsAccessor.Configure(this);

        return Task.CompletedTask;
    }

    public override Task OnShutdownAsync(ShutdownReason reason)
    {
        _idleTimer?.Dispose();
        OnTaskCompleted = null;
        foreach (var task in _runningTasks.Values)
            task.Cts?.Cancel();
        _client?.Dispose();
        SshToolsAccessor.Clear();
        return Task.CompletedTask;
    }

    public void Connect()
    {
        lock (_lock)
        {
            if (_client?.IsConnected == true) return;

            var keyPath = _config.ResolveKeyPath(_configDir);
            if (string.IsNullOrEmpty(keyPath) || !File.Exists(keyPath))
                throw new InvalidOperationException($"SSH 私钥不存在: {keyPath}");

            var keyFile = new PrivateKeyFile(keyPath);
            var connectionInfo = new ConnectionInfo(_config.Host, _config.Port, _config.Username,
                new PrivateKeyAuthenticationMethod(_config.Username, keyFile));

            _client = new SshClient(connectionInfo);
            _client.Connect();

            _idleTimer?.Dispose();
            _idleTimer = new Timer(OnIdleCheck, null,
                TimeSpan.FromSeconds(_config.IdleTimeoutSeconds),
                TimeSpan.FromSeconds(_config.IdleTimeoutSeconds));
        }
    }

    public string RegisterTask(string loopId, string command, CancellationTokenSource cts)
    {
        if (_client == null) throw new InvalidOperationException("SSH 未连接");

        var taskId = $"ssh-{loopId.Replace(':', '-')}-{Interlocked.Increment(ref _seq)}";
        var task = new SshTask
        {
            TaskId = taskId,
            LoopId = loopId,
            Command = command,
            Cts = cts
        };
        _runningTasks[taskId] = task;
        ResetIdleTimer();
        return taskId;
    }

    public void CompleteTask(string taskId, string? stdout, string? stderr, int? exitCode,
        SshTaskStatus status, string? error = null,
        string? stdoutFile = null, string? stderrFile = null)
    {
        if (_runningTasks.TryRemove(taskId, out var task))
        {
            task.Stdout = stdout;
            task.Stderr = stderr;
            task.StdoutFile = stdoutFile;
            task.StderrFile = stderrFile;
            task.ExitCode = exitCode;
            task.Status = status;
            task.Error = error;
            task.CompletedAt = DateTime.UtcNow;
            _completedTasks[taskId] = task;
            ResetIdleTimer();

            // 唤醒对应 loop
            OnTaskCompleted?.Invoke(task.LoopId);
        }
    }

    public bool TryGetTask(string taskId, out SshTask? task)
    {
        if (_runningTasks.TryGetValue(taskId, out task)) return true;
        return _completedTasks.TryGetValue(taskId, out task);
    }

    public List<SshTask> GetAllRunningTasks() => _runningTasks.Values.ToList();

    /// <summary>为指定 loop 兑现已完成任务通知，调用后清空该 loop 的已完成队列。</summary>
    public List<SshTask> DrainCompletedTasks(string loopId)
    {
        var result = new List<SshTask>();
        foreach (var kvp in _completedTasks)
        {
            if (kvp.Value.LoopId == loopId)
            {
                result.Add(kvp.Value);
                _completedTasks.TryRemove(kvp.Key, out _);
            }
        }
        return result;
    }

    private void ResetIdleTimer()
    {
        _idleTimer?.Change(TimeSpan.FromSeconds(_config.IdleTimeoutSeconds),
            TimeSpan.FromSeconds(_config.IdleTimeoutSeconds));
    }

    private void OnIdleCheck(object? _)
    {
        lock (_lock)
        {
            if (_runningTasks.IsEmpty && _client?.IsConnected == true)
            {
                _client.Disconnect();
            }
        }
    }

    private static int _seq;
}

/// <summary>静态访问器</summary>
public static class SshToolsAccessor
{
    public static SshGlobalComponent? Global { get; private set; }
    public static void Configure(SshGlobalComponent global) => Global = global;
    public static void Clear() => Global = null;
}
