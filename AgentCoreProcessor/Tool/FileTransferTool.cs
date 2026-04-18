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
        public string Description => "在主机 Storage 目录与远程 Linux 虚拟机之间传输文件。支持上传（主机→VM）和下载（VM→主机）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("方向", "upload（主机→VM）或 download（VM→主机）", 0),
            new("本地路径", "主机上的文件路径（相对 Storage/ 或绝对路径）", 1),
            new("远程路径", "VM 上的文件绝对路径", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(35);
        public bool AllowSubAgent => false;
        public PermissionLevel RequiredPermission => PermissionLevel.Elevated;

        private static readonly string ConfigPath =
            Path.Combine(PathConfig.StoragePath, "SSH", "RemoteShellConfig.json");

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var direction = (resolvedInputs.ElementAtOrDefault(0) ?? "").Trim().ToLower();
            var localRaw = resolvedInputs.ElementAtOrDefault(1) ?? "";
            var remotePath = resolvedInputs.ElementAtOrDefault(2) ?? "";

            if (direction != "upload" && direction != "download")
                return new ToolResult { Status = "failed", Error = "方向必须是 upload 或 download" };
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

            if (direction == "upload")
                return await UploadAsync(localPath, remotePath, host, port, username, keyPath, localRaw, ct);
            else
                return await DownloadAsync(localPath, remotePath, host, port, username, keyPath, localRaw, ct);
        }

        private async Task<ToolResult> UploadAsync(
            string localPath, string remotePath,
            string host, int port, string username, string keyPath,
            string displayPath, CancellationToken ct)
        {
            if (!File.Exists(localPath))
                return new ToolResult { Status = "failed", Error = $"本地文件不存在: {displayPath}" };

            var fileSize = new FileInfo(localPath).Length;
            var (sizeOk, sizeError) = FileAccessControl.CheckTransferSize(fileSize);
            if (!sizeOk)
                return new ToolResult { Status = "failed", Error = sizeError };

            var args = $"-i \"{keyPath}\" -o StrictHostKeyChecking=no -P {port} " +
                       $"\"{localPath}\" {username}@{host}:\"{remotePath}\"";
            return await RunScpAsync(args, $"已上传: {displayPath} → {remotePath} ({fileSize}字节)", ct);
        }

        private async Task<ToolResult> DownloadAsync(
            string localPath, string remotePath,
            string host, int port, string username, string keyPath,
            string displayPath, CancellationToken ct)
        {
            // 先查远程文件大小
            var sshArgs = $"-i \"{keyPath}\" -o StrictHostKeyChecking=no -o ConnectTimeout=10 " +
                          $"-p {port} {username}@{host} \"stat -c %s '{remotePath}' 2>/dev/null || echo -1\"";
            var sizeResult = await RunProcessAsync("ssh", sshArgs, 10000, ct);
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

            var args = $"-i \"{keyPath}\" -o StrictHostKeyChecking=no -P {port} " +
                       $"{username}@{host}:\"{remotePath}\" \"{localPath}\"";
            var result = await RunScpAsync(args, $"已下载: {remotePath} → {displayPath}", ct);

            if (result.IsSuccess && File.Exists(localPath))
            {
                var actualSize = new FileInfo(localPath).Length;
                result.Data = $"已下载: {remotePath} → {displayPath} ({actualSize}字节)";
            }
            return result;
        }

        private static async Task<ToolResult> RunScpAsync(string args, string successMsg, CancellationToken ct)
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "scp",
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var stderr = new StringBuilder();
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                process.Start();
                process.BeginErrorReadLine();

                var exited = await Task.Run(() => process.WaitForExit(30000), ct);
                if (!exited)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return new ToolResult { Status = "failed", Error = "传输超时（30秒）" };
                }

                if (process.ExitCode != 0)
                    return new ToolResult { Status = "failed", Error = $"SCP 失败: {stderr.ToString().Trim()}" };

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
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync(ct);
                process.WaitForExit(timeoutMs);
                return output;
            }
            catch { return "-1"; }
        }
    }
}