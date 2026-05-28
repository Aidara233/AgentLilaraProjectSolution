# Plugin.SshTools Implementation Plan

> **状态：已完成 (2026-05-27)** — 所有功能已实现

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create an SSH plugin providing remote command execution + file transfer to a PVE VM, with async task management and local workspace sandboxing.

**Architecture:** GlobalComponent (SshClient singleton + task registry) + LoopComponent (5 tools + per-loop remote workspace + completion notifications). Follows Plugin.NetworkTools static-accessor pattern. Config migrates from `Storage/SSH/` to `Storage/PluginData/SshTools.json`.

**输出策略:** 同步完成 → stdout/stderr 直接返回（截断 4000 字符），bot 需完整版自己 `command > file` 重定向。异步（含超时降级）→ 自动写入 `{workDir}/.task_{id}.stdout.txt` + `.stderr.txt`，通知只给摘要 + 文件路径。

**Tech Stack:** .NET 8, C#, SSH.NET 2025.1.0, AgentLilara.PluginSDK

---

## File Structure

```
Plugins/Plugin.SshTools/
├── Plugin.SshTools.csproj        # SDK ref + SSH.NET NuGet + CopyToHostPlugins
├── SshConfig.cs                  # 配置加载（SshTools.json）
├── SshGlobalComponent.cs         # Global: SshClient + ConcurrentDictionary<string,SshTask>
├── SshLoopComponent.cs           # Loop: 5工具 + 远端工作目录 + BuildPromptSection
├── SshTask.cs                    # 异步任务记录
└── Tools/
    ├── ExecTool.cs               # ssh_exec
    ├── UploadTool.cs             # ssh_upload
    ├── DownloadTool.cs           # ssh_download
    ├── CheckTaskTool.cs          # ssh_check
    └── KillTaskTool.cs           # ssh_kill
```

---

### Task 1: Create project scaffold and config

**Files:**
- Create: `Plugins/Plugin.SshTools/Plugin.SshTools.csproj`
- Create: `Plugins/Plugin.SshTools/SshConfig.cs`
- Create: `Plugins/Plugin.SshTools/Tools/` (directory)

- [ ] **Step 1: Create directory structure**

```bash
mkdir -p "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution/Plugins/Plugin.SshTools/Tools"
```

- [ ] **Step 2: Write Plugin.SshTools.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Plugin.SshTools</RootNamespace>
    <CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\AgentLilara.PluginSDK\AgentLilara.PluginSDK.csproj">
      <Private>false</Private>
    </ProjectReference>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="SSH.NET" Version="2025.1.0" />
  </ItemGroup>

  <Target Name="CopyToHostPlugins" AfterTargets="Build">
    <Copy SourceFiles="$(OutputPath)Plugin.SshTools.dll;$(OutputPath)Renci.SshNet.dll"
          DestinationFolder="$(SolutionDir)AgentCoreProcessor\bin\$(Configuration)\net8.0\Plugins\" />
  </Target>

</Project>
```

- [ ] **Step 3: Write SshConfig.cs**

```csharp
// Plugins/Plugin.SshTools/SshConfig.cs
using System.Text.Json;

namespace Plugin.SshTools;

public class SshConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string KeyPath { get; set; } = "";
    public int MaxOutputChars { get; set; } = 4000;
    public int MaxTimeoutSeconds { get; set; } = 120;
    public int IdleTimeoutSeconds { get; set; } = 300;
    public int ReconnectDelaySeconds { get; set; } = 5;

    public static SshConfig Load(string configDir)
    {
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "SshTools.json");

        if (!File.Exists(path))
        {
            // 尝试从旧路径迁移
            var legacyDir = Path.GetFullPath(Path.Combine(configDir, "..", "..", "SSH"));
            var legacyConfig = Path.Combine(legacyDir, "RemoteShellConfig.json");
            var legacyKey = Path.Combine(legacyDir, "pve-ALPAlpine", "key");

            if (File.Exists(legacyConfig))
            {
                var legacy = JsonSerializer.Deserialize<LegacySshConfig>(File.ReadAllText(legacyConfig));
                if (legacy != null)
                {
                    var newKeyDir = Path.Combine(configDir, "SshTools");
                    Directory.CreateDirectory(newKeyDir);
                    var newKeyPath = Path.Combine(newKeyDir, "key");
                    if (File.Exists(legacyKey) && !File.Exists(newKeyPath))
                        File.Copy(legacyKey, newKeyPath);

                    var migrated = new SshConfig
                    {
                        Host = legacy.Host,
                        Port = legacy.Port,
                        Username = legacy.Username,
                        KeyPath = "SshTools/key"
                    };
                    var json = JsonSerializer.Serialize(migrated, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(path, json);
                    return migrated;
                }
            }

            var defaults = new SshConfig();
            var defaultJson = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, defaultJson);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<SshConfig>(json, options) ?? new SshConfig();
        }
        catch
        {
            return new SshConfig();
        }
    }

    public string ResolveKeyPath(string configDir)
    {
        if (string.IsNullOrEmpty(KeyPath)) return "";
        if (Path.IsPathRooted(KeyPath)) return KeyPath;
        return Path.GetFullPath(Path.Combine(configDir, KeyPath));
    }

    private class LegacySshConfig
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string Username { get; set; } = "";
        public string KeyPath { get; set; } = "";
    }
}
```

- [ ] **Step 4: Add project to solution**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution" && dotnet sln add Plugins/Plugin.SshTools/Plugin.SshTools.csproj
```

- [ ] **Step 5: Build to verify scaffold**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution" && dotnet build Plugins/Plugin.SshTools/Plugin.SshTools.csproj
```

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution" && git add Plugins/Plugin.SshTools/ && git commit -m "feat: add Plugin.SshTools project scaffold and config"
```

---

### Task 2: Create SshTask model and GlobalComponent

**Files:**
- Create: `Plugins/Plugin.SshTools/SshTask.cs`
- Create: `Plugins/Plugin.SshTools/SshGlobalComponent.cs`

- [ ] **Step 1: Write SshTask.cs**

```csharp
// Plugins/Plugin.SshTools/SshTask.cs
namespace Plugin.SshTools;

public enum SshTaskStatus { Running, Completed, Failed, Killed, TimedOut }

public class SshTask
{
    public string TaskId { get; init; } = "";
    public string LoopId { get; init; } = "";
    public string Command { get; init; } = "";
    public SshTaskStatus Status { get; set; } = SshTaskStatus.Running;
    /// <summary>异步任务输出文件（远端路径），同步完成时为 null</summary>
    public string? StdoutFile { get; set; }
    public string? StderrFile { get; set; }
    /// <summary>同步完成时的内联输出（截断），异步完成时为 null</summary>
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public CancellationTokenSource? Cts { get; set; }
}
```

- [ ] **Step 2: Write SshGlobalComponent.cs**

```csharp
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
    private readonly ConcurrentQueue<SshTask> _completedTasks = new();

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
            _completedTasks.Enqueue(task);
            ResetIdleTimer();

            // 唤醒对应 loop
            OnTaskCompleted?.Invoke(task.LoopId);
        }
    }

    public bool TryGetTask(string taskId, out SshTask? task)
    {
        if (_runningTasks.TryGetValue(taskId, out task)) return true;
        task = _completedTasks.FirstOrDefault(t => t.TaskId == taskId);
        return task != null;
    }

    public List<SshTask> GetAllRunningTasks() => _runningTasks.Values.ToList();

    /// <summary>为指定 loop 兑现已完成任务通知，调用后清空该 loop 的已完成队列。</summary>
    public List<SshTask> DrainCompletedTasks(string loopId)
    {
        var result = new List<SshTask>();
        var remaining = new List<SshTask>();
        while (_completedTasks.TryDequeue(out var task))
        {
            if (task.LoopId == loopId)
                result.Add(task);
            else
                remaining.Add(task);
        }
        foreach (var t in remaining) _completedTasks.Enqueue(t);
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
```

- [ ] **Step 3: Build to verify**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution" && dotnet build Plugins/Plugin.SshTools/Plugin.SshTools.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution" && git add Plugins/Plugin.SshTools/ && git commit -m "feat: add SshTask model and GlobalComponent with SshClient lifecycle"
```

---

### Task 3: Create ExecTool (ssh_exec)

**Files:**
- Create: `Plugins/Plugin.SshTools/Tools/ExecTool.cs`

- [ ] **Step 1: Write ExecTool.cs**

```csharp
// Plugins/Plugin.SshTools/Tools/ExecTool.cs
using Renci.SshNet;
using AgentLilara.PluginSDK;

namespace Plugin.SshTools;

[ToolMeta(Group = "ssh", ContinueLoop = true, CapabilitySummary = "在远程服务器上执行命令")]
public class ExecTool : ITool
{
    private readonly SshGlobalComponent _global;
    private readonly string _workDir;
    private readonly string _loopId;

    public string Name => "ssh_exec";
    public string Description => "在远程服务器上执行 shell 命令。"
        + "同步完成时 stdout/stderr 直接返回（截断 " + MaxOutputChars + " 字符），需完整输出请用 command > file 重定向。"
        + "timeout=0 异步立即返回 task_id。timeout>0 同步等待，超时自动降级：原命令继续跑，"
        + "输出写入远端文件，下轮自动通知。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("command", "要执行的 shell 命令", 0),
        new("timeout", "等待秒数，默认 10，0=异步不等待", 1, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(_global.Config.MaxTimeoutSeconds + 5);

    private const int DefaultTimeoutSeconds = 10;
    public const int MaxOutputChars = 4000;

    public ExecTool(SshGlobalComponent global, string workDir, string loopId)
    {
        _global = global;
        _workDir = workDir;
        _loopId = loopId;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var command = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
        var timeoutStr = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

        if (string.IsNullOrEmpty(command))
            return Task.FromResult(Fail("command 不能为空"));

        var timeoutSeconds = DefaultTimeoutSeconds;
        if (int.TryParse(timeoutStr, out var t))
            timeoutSeconds = t;

        var client = _global.Client;
        if (client?.IsConnected != true)
            return Task.FromResult(Fail("SSH 未连接"));

        var fullCommand = $"cd {EscapeShellArg(_workDir)} && {command}";

        // 异步模式：后台线程创建命令、等待完成、写文件
        if (timeoutSeconds <= 0)
        {
            var taskId = LaunchAsync(client, fullCommand, command);
            return Task.FromResult(Ok($"{{\"status\":\"launched\",\"task_id\":\"{taskId}\"}}"));
        }

        // 同步模式
        var sshCmd = client.CreateCommand(fullCommand);
        var asyncResult = sshCmd.BeginExecute();
        var completed = asyncResult.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(timeoutSeconds));

        if (completed)
        {
            // 正常完成
            try
            {
                var stdout = sshCmd.EndExecute(asyncResult);
                var stderr = sshCmd.Error;
                var exitCode = sshCmd.ExitStatus ?? 0;
                sshCmd.Dispose();

                var result = new Dictionary<string, object>
                {
                    ["exit_code"] = exitCode,
                    ["stdout"] = Truncate(stdout, MaxOutputChars),
                    ["stderr"] = Truncate(stderr, MaxOutputChars)
                };
                if (stdout.Length > MaxOutputChars || stderr.Length > MaxOutputChars)
                    result["hint"] = "输出已截断，需完整内容请用 ssh_exec \"command > file\" 重定向后 ssh_download";

                return Task.FromResult(Ok(System.Text.Json.JsonSerializer.Serialize(result)));
            }
            catch (Exception ex)
            {
                sshCmd.Dispose();
                return Task.FromResult(Fail($"执行失败: {ex.Message}"));
            }
        }

        // 超时降级：sshCmd 不 dispose，交给后台线程继续等
        var fallbackTaskId = ContinueInBackground(sshCmd, asyncResult, command);
        return Task.FromResult(Ok(
            $"{{\"status\":\"async_fallback\",\"task_id\":\"{fallbackTaskId}\"," +
            $"\"reason\":\"超时 {timeoutSeconds}s，命令继续在远端执行，完成后自动通知\"}}"));
    }

    /// <summary>显式异步：后台线程创建新命令</summary>
    private string LaunchAsync(Renci.SshNet.SshClient client, string fullCommand, string displayCmd)
    {
        var cts = new CancellationTokenSource();
        var taskId = _global.RegisterTask(_loopId, displayCmd, cts);

        Task.Run(() =>
        {
            try
            {
                using var sshCmd = client.CreateCommand(fullCommand);
                var ar = sshCmd.BeginExecute();
                ar.AsyncWaitHandle.WaitOne();
                var stdout = sshCmd.EndExecute(ar);
                var stderr = sshCmd.Error;
                var exitCode = sshCmd.ExitStatus ?? 0;
                WriteOutputAndComplete(taskId, stdout, stderr, exitCode);
            }
            catch (Exception ex)
            {
                _global.CompleteTask(taskId, null, null, null, SshTaskStatus.Failed, ex.Message);
            }
        }, cts.Token);

        return taskId;
    }

    /// <summary>超时降级：接管已有 sshCmd，不创建新命令</summary>
    private string ContinueInBackground(Renci.SshNet.SshCommand sshCmd, IAsyncResult asyncResult, string displayCmd)
    {
        var cts = new CancellationTokenSource();
        var taskId = _global.RegisterTask(_loopId, displayCmd, cts);

        Task.Run(() =>
        {
            try
            {
                // 继续等待已开始的命令
                asyncResult.AsyncWaitHandle.WaitOne();
                var stdout = sshCmd.EndExecute(asyncResult);
                var stderr = sshCmd.Error;
                var exitCode = sshCmd.ExitStatus ?? 0;
                sshCmd.Dispose();
                WriteOutputAndComplete(taskId, stdout, stderr, exitCode);
            }
            catch (Exception ex)
            {
                try { sshCmd.Dispose(); } catch { }
                _global.CompleteTask(taskId, null, null, null, SshTaskStatus.Failed, ex.Message);
            }
        }, cts.Token);

        return taskId;
    }

    /// <summary>将异步输出写入远端文件，然后 CompleteTask</summary>
    private void WriteOutputAndComplete(string taskId, string stdout, string stderr, int exitCode)
    {
        var stdoutFile = "";
        var stderrFile = "";

        try
        {
            var client = _global.Client;
            if (client?.IsConnected == true)
            {
                if (!string.IsNullOrEmpty(stdout))
                {
                    stdoutFile = $"{_workDir}/.task_{taskId}.stdout.txt";
                    WriteRemoteFile(client, stdoutFile, stdout);
                }
                if (!string.IsNullOrEmpty(stderr))
                {
                    stderrFile = $"{_workDir}/.task_{taskId}.stderr.txt";
                    WriteRemoteFile(client, stderrFile, stderr);
                }
            }
        }
        catch { /* 写文件失败不影响任务完成通知 */ }

        _global.CompleteTask(taskId, null, null, exitCode, SshTaskStatus.Completed,
            stdoutFile: string.IsNullOrEmpty(stdoutFile) ? null : stdoutFile,
            stderrFile: string.IsNullOrEmpty(stderrFile) ? null : stderrFile);
    }

    private static void WriteRemoteFile(Renci.SshNet.SshClient client, string path, string content)
    {
        var escapedPath = EscapeShellArg(path);
        // 用 base64 安全写入（避免特殊字符/换行问题）
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        using var cmd = client.CreateCommand($"echo {EscapeShellArg(encoded)} | base64 -d > {escapedPath}");
        cmd.Execute();
    }

    private static string EscapeShellArg(string s) => "'" + s.Replace("'", "'\\''") + "'";
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
```

- [ ] **Step 2: Build to verify**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution" && dotnet build Plugins/Plugin.SshTools/Plugin.SshTools.csproj
```

Expected: Build may fail — SshLoopComponent not created yet. Wait for Task 4 to build together.

---

### Task 4: Create SshLoopComponent and wire all tools

**Files:**
- Create: `Plugins/Plugin.SshTools/SshLoopComponent.cs`
- Create: `Plugins/Plugin.SshTools/Tools/CheckTaskTool.cs`
- Create: `Plugins/Plugin.SshTools/Tools/KillTaskTool.cs`
- Create: `Plugins/Plugin.SshTools/Tools/UploadTool.cs`
- Create: `Plugins/Plugin.SshTools/Tools/DownloadTool.cs`

- [ ] **Step 1: Write CheckTaskTool.cs**

```csharp
// Plugins/Plugin.SshTools/Tools/CheckTaskTool.cs
using AgentLilara.PluginSDK;

namespace Plugin.SshTools;

[ToolMeta(Group = "ssh", ContinueLoop = true, CapabilitySummary = "查询异步SSH任务状态")]
public class CheckTaskTool : ITool
{
    private readonly SshGlobalComponent _global;

    public string Name => "ssh_check";
    public string Description => "查询 SSH 异步任务状态。不传 task_id 列出所有进行中/已完成的任务。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("task_id", "任务ID，不传列出全部", 0, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public CheckTaskTool(SshGlobalComponent global) { _global = global; }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var taskId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";

        if (!string.IsNullOrEmpty(taskId))
        {
            if (_global.TryGetTask(taskId, out var task) && task != null)
                return Task.FromResult(Ok(FormatTask(task)));
            return Task.FromResult(Fail($"任务不存在: {taskId}"));
        }

        var running = _global.GetAllRunningTasks();
        var items = running.Select(FormatTask).ToList();
        return Task.FromResult(Ok(System.Text.Json.JsonSerializer.Serialize(items)));
    }

    private static string FormatTask(SshTask t)
    {
        var result = new Dictionary<string, object>
        {
            ["task_id"] = t.TaskId,
            ["command"] = t.Command,
            ["status"] = t.Status.ToString().ToLower(),
            ["started_at"] = t.StartedAt.ToString("o")
        };
        if (t.ExitCode.HasValue) result["exit_code"] = t.ExitCode.Value;
        if (t.Stdout != null) result["stdout"] = Truncate(t.Stdout, 2000);
        if (t.Stderr != null) result["stderr"] = Truncate(t.Stderr, 500);
        if (t.StdoutFile != null) result["stdout_file"] = t.StdoutFile;
        if (t.StderrFile != null) result["stderr_file"] = t.StderrFile;
        if (t.Error != null) result["error"] = t.Error;
        if (t.CompletedAt.HasValue) result["completed_at"] = t.CompletedAt.Value.ToString("o");
        return System.Text.Json.JsonSerializer.Serialize(result);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
```

- [ ] **Step 2: Write KillTaskTool.cs**

```csharp
// Plugins/Plugin.SshTools/Tools/KillTaskTool.cs
using AgentLilara.PluginSDK;

namespace Plugin.SshTools;

[ToolMeta(Group = "ssh", ContinueLoop = true, CapabilitySummary = "杀掉异步SSH任务")]
public class KillTaskTool : ITool
{
    private readonly SshGlobalComponent _global;

    public string Name => "ssh_kill";
    public string Description => "杀掉指定的异步 SSH 任务。远端进程会收到 SIGTERM。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("task_id", "要杀的任务ID", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public KillTaskTool(SshGlobalComponent global) { _global = global; }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var taskId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        if (string.IsNullOrEmpty(taskId))
            return Task.FromResult(Fail("task_id 不能为空"));

        if (!_global.TryGetTask(taskId, out var task) || task == null)
            return Task.FromResult(Fail($"任务不存在: {taskId}"));

        task.Cts?.Cancel();
        _global.CompleteTask(taskId, null, null, null, SshTaskStatus.Killed, "由 ssh_kill 终止");
        return Task.FromResult(Ok($"{{\"task_id\":\"{taskId}\",\"status\":\"killed\"}}"));
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
```

- [ ] **Step 3: Write UploadTool.cs**

```csharp
// Plugins/Plugin.SshTools/Tools/UploadTool.cs
using AgentLilara.PluginSDK;

namespace Plugin.SshTools;

[ToolMeta(Group = "ssh", ContinueLoop = true, CapabilitySummary = "上传文件到远程服务器")]
public class UploadTool : ITool
{
    private readonly SshGlobalComponent _global;
    private readonly string _workDir;
    private readonly string _workspaceDir;
    private readonly string _loopId;

    public string Name => "ssh_upload";
    public string Description => "上传本地 workspace 文件到远程服务器。"
        + "timeout=0 异步立即返回。默认目标为远端工作目录。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("local_path", "本地文件路径（限制在 workspace 内）", 0),
        new("remote_path", "远端目标路径，默认工作目录", 1, isRequired: false),
        new("timeout", "等待秒数，默认 30，0=异步", 2, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(_global.Config.MaxTimeoutSeconds + 10);

    private const int DefaultTimeoutSeconds = 30;

    public UploadTool(SshGlobalComponent global, string workDir, string workspaceDir, string loopId)
    {
        _global = global;
        _workDir = workDir;
        _workspaceDir = workspaceDir;
        _loopId = loopId;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var localPath = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
        var remotePath = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
        var timeoutStr = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";

        if (string.IsNullOrEmpty(localPath))
            return Task.FromResult(Fail("local_path 不能为空"));

        // 沙箱校验
        var fullLocal = Path.GetFullPath(Path.Combine(_workspaceDir, localPath));
        var fullWorkspace = Path.GetFullPath(_workspaceDir);
        if (!fullLocal.StartsWith(fullWorkspace + Path.DirectorySeparatorChar)
            && fullLocal != fullWorkspace)
            return Task.FromResult(Fail($"local_path 必须在 workspace 内: {_workspaceDir}"));

        if (!File.Exists(fullLocal) && !Directory.Exists(fullLocal))
            return Task.FromResult(Fail($"本地路径不存在: {fullLocal}"));

        var fullRemote = !string.IsNullOrEmpty(remotePath)
            ? (remotePath.StartsWith('/') ? remotePath : $"{_workDir}/{remotePath}")
            : _workDir;

        var client = _global.Client;
        if (client?.IsConnected != true)
            return Task.FromResult(Fail("SSH 未连接"));

        var timeoutSeconds = DefaultTimeoutSeconds;
        if (int.TryParse(timeoutStr, out var t))
            timeoutSeconds = t;

        var displayCmd = $"scp -r {localPath} → {fullRemote}";

        if (timeoutSeconds <= 0)
        {
            var taskId = LaunchUploadAsync(client, fullLocal, fullRemote, displayCmd);
            return Task.FromResult(Ok($"{{\"status\":\"launched\",\"task_id\":\"{taskId}\"}}"));
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

            using var sftp = new Renci.SshNet.SftpClient(client.ConnectionInfo);
            sftp.Connect();

            if (Directory.Exists(fullLocal))
                UploadDirectory(sftp, fullLocal, fullRemote);
            else
                UploadFile(sftp, fullLocal, fullRemote);

            sftp.Disconnect();
            return Task.FromResult(Ok($"{{\"status\":\"completed\",\"local\":\"{localPath}\",\"remote\":\"{fullRemote}\"}}"));
        }
        catch (OperationCanceledException)
        {
            var taskId = LaunchUploadAsync(client, fullLocal, fullRemote, displayCmd);
            return Task.FromResult(Ok($"{{\"status\":\"async_fallback\",\"task_id\":\"{taskId}\",\"reason\":\"超时 {timeoutSeconds}s\"}}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail($"上传失败: {ex.Message}"));
        }
    }

    private string LaunchUploadAsync(Renci.SshNet.SshClient client, string local, string remote, string displayCmd)
    {
        var cts = new CancellationTokenSource();
        var taskId = _global.RegisterTask(_loopId, displayCmd, cts);

        Task.Run(() =>
        {
            try
            {
                using var sftp = new Renci.SshNet.SftpClient(client.ConnectionInfo);
                sftp.Connect();
                if (Directory.Exists(local))
                    UploadDirectory(sftp, local, remote);
                else
                    UploadFile(sftp, local, remote);
                sftp.Disconnect();
                _global.CompleteTask(taskId, null, null, 0, SshTaskStatus.Completed);
            }
            catch (Exception ex)
            {
                _global.CompleteTask(taskId, null, null, null, SshTaskStatus.Failed, ex.Message);
            }
        }, cts.Token);

        return taskId;
    }

    private static void UploadFile(Renci.SshNet.SftpClient sftp, string local, string remote)
    {
        var remoteDir = Path.GetDirectoryName(remote)?.Replace('\\', '/') ?? ".";
        if (!string.IsNullOrEmpty(remoteDir) && !sftp.Exists(remoteDir))
            sftp.CreateDirectory(remoteDir);
        using var fs = File.OpenRead(local);
        sftp.UploadFile(fs, remote.Replace('\\', '/'), true);
    }

    private static void UploadDirectory(Renci.SshNet.SftpClient sftp, string local, string remote)
    {
        var remoteDir = remote.Replace('\\', '/');
        if (!sftp.Exists(remoteDir))
            sftp.CreateDirectory(remoteDir);
        foreach (var file in Directory.GetFiles(local))
            UploadFile(sftp, file, $"{remoteDir}/{Path.GetFileName(file)}");
        foreach (var dir in Directory.GetDirectories(local))
            UploadDirectory(sftp, dir, $"{remoteDir}/{Path.GetFileName(dir)}");
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
```

- [ ] **Step 4: Write DownloadTool.cs**

```csharp
// Plugins/Plugin.SshTools/Tools/DownloadTool.cs
using AgentLilara.PluginSDK;
using Renci.SshNet;

namespace Plugin.SshTools;

[ToolMeta(Group = "ssh", ContinueLoop = true, CapabilitySummary = "从远程服务器下载文件")]
public class DownloadTool : ITool
{
    private readonly SshGlobalComponent _global;
    private readonly string _workDir;
    private readonly string _workspaceDir;
    private readonly string _loopId;

    public string Name => "ssh_download";
    public string Description => "从远程服务器下载文件到本地 workspace。"
        + "timeout=0 异步立即返回。默认源路径为远端工作目录。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("remote_path", "远端文件/目录路径", 0),
        new("local_path", "本地目标路径（限制在 workspace 内），默认 workspace 根", 1, isRequired: false),
        new("timeout", "等待秒数，默认 30，0=异步", 2, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(_global.Config.MaxTimeoutSeconds + 10);

    private const int DefaultTimeoutSeconds = 30;

    public DownloadTool(SshGlobalComponent global, string workDir, string workspaceDir, string loopId)
    {
        _global = global;
        _workDir = workDir;
        _workspaceDir = workspaceDir;
        _loopId = loopId;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var remotePath = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
        var localPath = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
        var timeoutStr = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";

        if (string.IsNullOrEmpty(remotePath))
            return Task.FromResult(Fail("remote_path 不能为空"));

        var fullRemote = remotePath.StartsWith('/') ? remotePath : $"{_workDir}/{remotePath}";

        if (string.IsNullOrEmpty(localPath))
            localPath = ".";
        var fullLocal = Path.GetFullPath(Path.Combine(_workspaceDir, localPath));
        var fullWorkspace = Path.GetFullPath(_workspaceDir);
        if (!fullLocal.StartsWith(fullWorkspace + Path.DirectorySeparatorChar) && fullLocal != fullWorkspace)
            return Task.FromResult(Fail($"local_path 必须在 workspace 内: {_workspaceDir}"));

        var client = _global.Client;
        if (client?.IsConnected != true)
            return Task.FromResult(Fail("SSH 未连接"));

        var timeoutSeconds = DefaultTimeoutSeconds;
        if (int.TryParse(timeoutStr, out var t))
            timeoutSeconds = t;

        var displayCmd = $"scp {fullRemote} → {localPath}";

        if (timeoutSeconds <= 0)
        {
            var taskId = LaunchDownloadAsync(client, fullRemote, fullLocal, displayCmd);
            return Task.FromResult(Ok($"{{\"status\":\"launched\",\"task_id\":\"{taskId}\"}}"));
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cts.Token);

            using var sftp = new SftpClient(client.ConnectionInfo);
            sftp.Connect();
            DownloadRemote(sftp, fullRemote, fullLocal);
            sftp.Disconnect();
            return Task.FromResult(Ok($"{{\"status\":\"completed\",\"remote\":\"{fullRemote}\",\"local\":\"{localPath}\"}}"));
        }
        catch (OperationCanceledException)
        {
            var taskId = LaunchDownloadAsync(client, fullRemote, fullLocal, displayCmd);
            return Task.FromResult(Ok($"{{\"status\":\"async_fallback\",\"task_id\":\"{taskId}\",\"reason\":\"超时 {timeoutSeconds}s\"}}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail($"下载失败: {ex.Message}"));
        }
    }

    private string LaunchDownloadAsync(SshClient client, string remote, string local, string displayCmd)
    {
        var cts = new CancellationTokenSource();
        var taskId = _global.RegisterTask(_loopId, displayCmd, cts);

        Task.Run(() =>
        {
            try
            {
                using var sftp = new SftpClient(client.ConnectionInfo);
                sftp.Connect();
                DownloadRemote(sftp, remote, local);
                sftp.Disconnect();
                _global.CompleteTask(taskId, null, null, 0, SshTaskStatus.Completed);
            }
            catch (Exception ex)
            {
                _global.CompleteTask(taskId, null, null, null, SshTaskStatus.Failed, ex.Message);
            }
        }, cts.Token);

        return taskId;
    }

    private static void DownloadRemote(SftpClient sftp, string remote, string local)
    {
        remote = remote.Replace('\\', '/');
        if (sftp.Exists(remote))
        {
            var attr = sftp.GetAttributes(remote);
            if (attr.IsDirectory)
            {
                Directory.CreateDirectory(local);
                foreach (var entry in sftp.ListDirectory(remote))
                {
                    if (entry.Name == "." || entry.Name == "..") continue;
                    DownloadRemote(sftp, $"{remote}/{entry.Name}",
                        Path.Combine(local, entry.Name));
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(local) ?? ".");
                using var fs = File.Create(local);
                sftp.DownloadFile(remote, fs);
            }
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
```

- [ ] **Step 5: Write SshLoopComponent.cs**

```csharp
// Plugins/Plugin.SshTools/SshLoopComponent.cs
using System.Text;
using AgentLilara.PluginSDK;

namespace Plugin.SshTools;

[Component(Name = "ssh-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Enabled,
    Review = Applicability.Disabled, SubAgent = Applicability.Enabled)]
public class SshLoopComponent : LoopComponentBase
{
    private ILoopComponentContext _ctx = null!;
    private string _loopId = "";
    private string _workDir = "";
    private List<SshTask> _pendingNotifications = new();

    private ExecTool? _exec;
    private UploadTool? _upload;
    private DownloadTool? _download;
    private CheckTaskTool? _check;
    private KillTaskTool? _kill;

    public override ComponentMeta Meta => new()
    {
        Name = "ssh-tools",
        Description = "SSH 远程执行和文件传输",
        DefaultEnabled = true,
        PromptPriority = 40
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_exec != null) yield return _exec;
            if (_upload != null) yield return _upload;
            if (_download != null) yield return _download;
            if (_check != null) yield return _check;
            if (_kill != null) yield return _kill;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _ctx = context;
        _loopId = context.LoopId;

        var global = SshToolsAccessor.Global
            ?? throw new InvalidOperationException("SshGlobalComponent 未初始化");

        var workspaceDir = context.Storage.WorkspaceDirectory;
        _workDir = $"/tmp/agent-lilara/{CleanLoopId(_loopId)}";

        // 确保远端工作目录存在
        EnsureRemoteWorkDir(global, _workDir);

        _exec = new ExecTool(global, _workDir, _loopId);
        _upload = new UploadTool(global, _workDir, workspaceDir, _loopId);
        _download = new DownloadTool(global, _workDir, workspaceDir, _loopId);
        _check = new CheckTaskTool(global);
        _kill = new KillTaskTool(global);

        return Task.CompletedTask;
    }

    public override Task OnBeforeInvokeAsync()
    {
        var global = SshToolsAccessor.Global;
        if (global != null)
        {
            var completed = global.DrainCompletedTasks(_loopId);
            _pendingNotifications.AddRange(completed);
        }
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        var global = SshToolsAccessor.Global;
        var sb = new StringBuilder();

        // 连接状态
        var connStatus = global?.Client?.IsConnected == true ? "已连接" : "未连接";
        sb.AppendLine($"[SSH] {global?.Config.Host}:{global?.Config.Port} ({connStatus})");
        sb.AppendLine($"远端工作目录: {_workDir}");
        sb.AppendLine("提示: ssh_exec 同步输出截断 " + ExecTool.MaxOutputChars + " 字符，"
            + "需完整输出请用 `command > file` 重定向后 ssh_download。"
            + "timeout=0 异步执行，超时自动降级，输出写入远端文件，任务完成自动唤醒通知。");

        // 异步任务完成通知
        if (_pendingNotifications.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[SSH 任务完成]");
            foreach (var n in _pendingNotifications)
            {
                if (n.Status == SshTaskStatus.Completed)
                {
                    sb.Append($"- {n.TaskId}: \"{n.Command}\" → exit {n.ExitCode ?? 0}");
                    if (n.StdoutFile != null)
                        sb.Append($" | stdout: {n.StdoutFile}");
                    if (n.StderrFile != null)
                        sb.Append($" | stderr: {n.StderrFile}");
                    sb.AppendLine();
                }
                else if (n.Status == SshTaskStatus.Failed)
                    sb.AppendLine($"- {n.TaskId}: \"{n.Command}\" → 失败: {n.Error}");
                else if (n.Status == SshTaskStatus.Killed)
                    sb.AppendLine($"- {n.TaskId}: \"{n.Command}\" → 已终止");
                else if (n.Status == SshTaskStatus.TimedOut)
                    sb.AppendLine($"- {n.TaskId}: \"{n.Command}\" → 超时");
            }
            _pendingNotifications.Clear();
        }

        return sb.ToString().TrimEnd();
    }

    private static void EnsureRemoteWorkDir(SshGlobalComponent global, string workDir)
    {
        var client = global.Client;
        if (client?.IsConnected != true) return;

        try
        {
            var escaped = "'" + workDir.Replace("'", "'\\''") + "'";
            using var cmd = client.CreateCommand($"mkdir -p {escaped}");
            cmd.Execute();
        }
        catch { /* 首轮失败不阻塞 */ }
    }

    private static string CleanLoopId(string loopId)
    {
        // 替换非法字符为下划线
        var sb = new StringBuilder();
        foreach (var c in loopId)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }
}
```

- [ ] **Step 6: Build to verify**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution" && dotnet build Plugins/Plugin.SshTools/Plugin.SshTools.csproj
```

Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution" && git add Plugins/Plugin.SshTools/ && git commit -m "feat: add SshLoopComponent and all 5 tools"
```

---

### Task 5: Full solution build and smoke test

**Files:**
- Modify: None (verification only)

- [ ] **Step 1: Build entire solution**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution" && dotnet build
```

Expected: 0 errors.

- [ ] **Step 2: Kill existing process and run in test mode**

```bash
cmd //c "taskkill /IM AgentCoreProcessor.exe /T /F" 2>/dev/null
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution/AgentCoreProcessor" && dotnet run -- --test --delay 2
```

Observe startup logs: verify that `ssh-tools-global` and `ssh-tools` components are registered, and SshTools.json is created.

- [ ] **Step 3: Verify config migration**

Check that `Storage/PluginData/SshTools.json` exists and contains migrated values from the old `Storage/SSH/RemoteShellConfig.json`.

- [ ] **Step 4: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution" && git commit -m "verify: Plugin.SshTools builds and loads successfully"
```

---

### Task 6: Clean up old SSH artifacts and settings

**Files:**
- Modify: `.claude/settings.local.json`

- [ ] **Step 1: Remove old SSH Bash permissions from settings.local.json**

Remove lines 134, 137, 139 (the `Bash(ssh *)` wildcard and two specific SSH command allowlists).

- [ ] **Step 2: Mark old SSH directory as deprecated**

Add a `Storage/SSH/.DEPRECATED` marker file with content: "Moved to Storage/PluginData/SshTools.json — delete this directory once migration is confirmed."

- [ ] **Step 3: Commit**

```bash
cd "E:/Workspace/AgentLilaraProject/AgentLilaraProjectSolution" && git add ../.claude/settings.local.json ../Storage/SSH/.DEPRECATED && git commit -m "chore: deprecate old SSH config, clean up ssh permissions"
```

---

## Spec Coverage Check

| Spec Requirement | Task |
|---|---|
| GlobalComponent + SshClient 单例 + 唤醒回调 | Task 2 |
| LoopComponent + 5 工具 | Task 4 |
| ssh_exec 同步+超时复用 sshCmd 降级 | Task 3 |
| ssh_exec 异步输出写远端文件 | Task 3 |
| ssh_exec 同步内联返回（截断 4000 字符 + hint） | Task 3 |
| ssh_upload / ssh_download | Task 4 |
| ssh_check / ssh_kill（含文件路径） | Task 4 |
| 远端工作目录隔离 `/tmp/agent-lilara/{loopId}/` | Task 4 |
| 本地 workspace 沙箱 | Task 4 (UploadTool/DownloadTool) |
| 异步任务管理 (ConcurrentDictionary + CompleteTask 唤醒) | Task 2 |
| BuildPromptSection 注入连接状态 + 用法提示 + 文件路径通知 | Task 4 |
| 配置迁移（旧 Storage/SSH/ → PluginData/SshTools.json） | Task 1 |
| 复用现有 SSH 私钥 | Task 1 (SshConfig.Load migration) |
| 空闲超时断开 + 重连 | Task 2 |
| SSH.NET 2025.1.0 | Task 1 |
| 清理旧 SSH 权限和目录标记 | Task 6 |
