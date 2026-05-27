// Plugins/Plugin.SshTools/Tools/ExecTool.cs
using Renci.SshNet;
using System.Text;
using System.Text.Json;
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

                return Task.FromResult(Ok(JsonSerializer.Serialize(result)));
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
            }
            finally { cts.Dispose(); }
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
            }
            finally { cts.Dispose(); }
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
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        using var cmd = client.CreateCommand($"echo {EscapeShellArg(encoded)} | base64 -d > {escapedPath}");
        cmd.Execute();
    }

    private static string EscapeShellArg(string s) => "'" + s.Replace("'", "'\\''") + "'";
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
