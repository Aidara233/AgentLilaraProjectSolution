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
            finally { cts.Dispose(); }
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
