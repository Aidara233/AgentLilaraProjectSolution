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
            finally { cts.Dispose(); }
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
                    DownloadRemote(sftp, $"{remote}/{entry.Name}", Path.Combine(local, entry.Name));
                }
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(local) ?? ".");
                using var fs = File.Create(local);
                sftp.DownloadFile(remote, fs);
            }
        }
        else
        {
            throw new FileNotFoundException($"远端路径不存在: {remote}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
