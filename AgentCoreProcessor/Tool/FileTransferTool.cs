using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using Newtonsoft.Json.Linq;

namespace AgentCoreProcessor.Tool
{
    internal class FileTransferTool : ITool
    {
        public string Name => "文件传输";
        public string Description => "在主机 Storage 目录与远程 Linux 虚拟机之间传输文件。支持上传和下载";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("方向", "upload 或 download（也可以写 上传 或 下载）", 0),
            new("本地路径", "主机上的文件路径（相对 Storage/ 的路径，如 Workspace/test.txt）", 1),
            new("远程路径", "VM 上的文件绝对路径（如 /tmp/test.txt）", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(35);
        public bool AllowSubAgent => false;
        public PermissionLevel RequiredPermission => PermissionLevel.Elevated;
        public bool ContinueLoop => true;
        public string? CapabilitySummary => "在本地和远程服务器之间传输文件";

        private static readonly string ConfigPath =
            Path.Combine(PathConfig.StoragePath, "SSH", "RemoteShellConfig.json");

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var directionRaw = (resolvedInputs.ElementAtOrDefault(0) ?? "").Trim().ToLower();
            var direction = directionRaw switch
            {
                "upload" or "上传" => "upload",
                "download" or "下载" => "download",
                _ => ""
            };
            var localRaw = resolvedInputs.ElementAtOrDefault(1) ?? "";
            var remotePath = resolvedInputs.ElementAtOrDefault(2) ?? "";

            if (string.IsNullOrEmpty(direction))
                return new ToolResult { Status = "failed", Error = "方向必须是 upload/上传 或 download/下载" };
            if (string.IsNullOrWhiteSpace(localRaw))
                return new ToolResult { Status = "failed", Error = "本地路径不能为空" };
            if (string.IsNullOrWhiteSpace(remotePath))
                return new ToolResult { Status = "failed", Error = "远程路径不能为空" };

            var localPath = FileAccessControl.ResolvePath(localRaw);
            var (allowed, error) = FileAccessControl.CheckAccess(localPath);
            if (!allowed)
                return new ToolResult { Status = "failed", Error = error };

            if (!File.Exists(ConfigPath))
                return new ToolResult { Status = "failed", Error = "SSH 配置文件不存在" };

            JObject config;
            try { config = JObject.Parse(File.ReadAllText(ConfigPath)); }
            catch { return new ToolResult { Status = "failed", Error = "SSH 配置文件格式错误" }; }

            var host = config["host"]?.ToString() ?? "";
            var port = config["port"]?.Value<int>() ?? 22;
            var username = config["username"]?.ToString() ?? "root";
            var keyRelPath = config["keyPath"]?.ToString() ?? "";
            var keyPath = Path.Combine(PathConfig.StoragePath, keyRelPath);
            if (!File.Exists(keyPath))
                return new ToolResult { Status = "failed", Error = "SSH 私钥文件不存在" };

            var (sshBin, scpBin) = ResolveBinaries(config);

            if (direction == "upload")
                return await UploadAsync(localPath, remotePath, host, port, username, keyPath, localRaw, scpBin, ct);
            else
                return await DownloadAsync(localPath, remotePath, host, port, username, keyPath, localRaw, sshBin, scpBin, ct);
        }

        private static (string ssh, string scp) ResolveBinaries(JObject config)
        {
            var configured = config["sshPath"]?.ToString();
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                var dir = Path.GetDirectoryName(configured)!;
                var scpPath = Path.Combine(dir, "scp.exe");
                return (configured, File.Exists(scpPath) ? scpPath : "scp");
            }

            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "usr", "bin"),
                @"D:\Program Files\Git\usr\bin",
            };
            foreach (var dir in candidates)
            {
                var ssh = Path.Combine(dir, "ssh.exe");
                var scp = Path.Combine(dir, "scp.exe");
                if (File.Exists(ssh) && File.Exists(scp))
                    return (ssh, scp);
            }

            return ("ssh", "scp");
        }

        private async Task<ToolResult> UploadAsync(
            string localPath, string remotePath,
            string host, int port, string username, string keyPath,
            string displayPath, string scpBin, CancellationToken ct)
        {
            if (!File.Exists(localPath))
                return new ToolResult { Status = "failed", Error = $"本地文件不存在: {displayPath}" };

            var fileSize = new FileInfo(localPath).Length;
            var (sizeOk, sizeError) = FileAccessControl.CheckTransferSize(fileSize);
            if (!sizeOk)
                return new ToolResult { Status = "failed", Error = sizeError };

            var args = $"-i \"{keyPath}\" -o StrictHostKeyChecking=no -o BatchMode=yes -P {port} " +
                       $"\"{localPath}\" {username}@{host}:\"{remotePath}\"";
            return await RunScpAsync(scpBin, args, $"已上传: {displayPath} → {remotePath} ({fileSize}字节)", ct);
        }

        private async Task<ToolResult> DownloadAsync(
            string localPath, string remotePath,
            string host, int port, string username, string keyPath,
            string displayPath, string sshBin, string scpBin, CancellationToken ct)
        {
            var sshArgs = $"-i \"{keyPath}\" -o StrictHostKeyChecking=no -o BatchMode=yes -o ConnectTimeout=10 " +
                          $"-p {port} {username}@{host} \"stat -c %s '{remotePath}' 2>/dev/null || echo -1\"";
            var sizeResult = await RunProcessAsync(sshBin, sshArgs, 10000, ct);
            if (long.TryParse(sizeResult.Trim(), out var remoteSize) && remoteSize >= 0)
            {
                var (sizeOk, sizeError) = FileAccessControl.CheckTransferSize(remoteSize);
                if (!sizeOk)
                    return new ToolResult { Status = "failed", Error = sizeError };

                if (FileAccessControl.IsWorkspacePath(localPath))
                {
                    var (capOk, capError) = FileAccessControl.CheckWorkspaceCapacity(remoteSize);
                    if (!capOk)
                        return new ToolResult { Status = "failed", Error = capError };
                }
            }

            var dir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var args = $"-i \"{keyPath}\" -o StrictHostKeyChecking=no -o BatchMode=yes -P {port} " +
                       $"{username}@{host}:\"{remotePath}\" \"{localPath}\"";
            var result = await RunScpAsync(scpBin, args, $"已下载: {remotePath} → {displayPath}", ct);

            if (result.IsSuccess && File.Exists(localPath))
            {
                var actualSize = new FileInfo(localPath).Length;
                result.Data = $"已下载: {remotePath} → {displayPath} ({actualSize}字节)";
            }
            return result;
        }

        private static async Task<ToolResult> RunScpAsync(string scpBin, string args, string successMsg, CancellationToken ct)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = scpBin,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                process.EnableRaisingEvents = true;
                process.Start();
                process.StandardInput.Close();

                var stderrTask = process.StandardError.ReadToEndAsync();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(30000);

                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return new ToolResult { Status = "failed", Error = "传输超时（30秒）" };
                }

                var stderr = await stderrTask;

                if (process.ExitCode != 0)
                    return new ToolResult { Status = "failed", Error = $"SCP 失败(退出码={process.ExitCode}): {stderr.Trim()}" };

                return new ToolResult { Status = "success", Data = successMsg };
            }
            catch (Exception ex)
            {
                return new ToolResult { Status = "failed", Error = $"SCP 异常: {ex.Message}" };
            }
        }

        private static async Task<string> RunProcessAsync(string fileName, string args, int timeoutMs, CancellationToken ct)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                process.EnableRaisingEvents = true;
                process.Start();
                process.StandardInput.Close();

                var outputTask = process.StandardOutput.ReadToEndAsync();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);

                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return "-1";
                }

                return await outputTask;
            }
            catch { return "-1"; }
        }
    }
}
