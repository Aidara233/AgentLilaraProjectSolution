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
    internal class RemoteShellTool : ITool
    {
        public string Name => "远程终端";
        public string Description => "在隔离的 Linux 虚拟机上执行 shell 命令。可用于运行脚本、安装软件包、编译代码等。输出会被截断到配置的最大长度";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("命令", "要执行的 shell 命令", 0),
            new("超时秒数", "命令最大执行时间（秒），默认30，最大60", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(65);
        public bool AllowSubAgent => false;
        public PermissionLevel RequiredPermission => PermissionLevel.Elevated;
        public bool ContinueLoop => true;
        public bool RetainResult => true;
        public string? CapabilitySummary => "在远程服务器上执行命令";

        private static readonly string ConfigPath =
            Path.Combine(PathConfig.StoragePath, "SSH", "RemoteShellConfig.json");

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var command = resolvedInputs.ElementAtOrDefault(0) ?? "";
            if (string.IsNullOrWhiteSpace(command))
                return new ToolResult { Status = "failed", Error = "命令不能为空" };

            int timeoutSeconds = 30;
            var timeoutStr = resolvedInputs.ElementAtOrDefault(1);
            if (!string.IsNullOrWhiteSpace(timeoutStr) && int.TryParse(timeoutStr, out var parsed))
                timeoutSeconds = parsed;

            if (!File.Exists(ConfigPath))
                return new ToolResult { Status = "failed", Error = "SSH 配置文件不存在" };

            JObject config;
            try { config = JObject.Parse(File.ReadAllText(ConfigPath)); }
            catch { return new ToolResult { Status = "failed", Error = "SSH 配置文件格式错误" }; }

            var host = config["host"]?.ToString() ?? "";
            var port = config["port"]?.Value<int>() ?? 22;
            var username = config["username"]?.ToString() ?? "root";
            var maxOutput = config["maxOutputChars"]?.Value<int>() ?? 4000;
            var maxTimeout = config["maxTimeoutSeconds"]?.Value<int>() ?? 60;

            timeoutSeconds = Math.Clamp(timeoutSeconds, 1, maxTimeout);

            var keyRelPath = config["keyPath"]?.ToString() ?? "";
            var keyPath = Path.Combine(PathConfig.StoragePath, keyRelPath);
            if (!File.Exists(keyPath))
                return new ToolResult { Status = "failed", Error = "SSH 私钥文件不存在" };

            var sshArgs = $"-i \"{keyPath}\" -o StrictHostKeyChecking=no -o ConnectTimeout=10 " +
                          $"-p {port} {username}@{host} {EscapeCommand(command)}";

            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = "ssh",
                    Arguments = sshArgs,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                process.EnableRaisingEvents = true;
                process.Start();

                // 并行读取 stdout/stderr + 等待退出，避免 BeginOutputReadLine 死锁
                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutSeconds * 1000);

                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    var partialOut = stdoutTask.IsCompleted ? stdoutTask.Result : "";
                    return new ToolResult
                    {
                        Status = "failed",
                        Error = $"命令执行超时（{timeoutSeconds}秒）",
                        Data = Truncate(partialOut, maxOutput)
                    };
                }

                var output = await stdoutTask;
                var errors = await stderrTask;
                var exitCode = process.ExitCode;

                var result = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(output))
                    result.Append(Truncate(output, maxOutput));
                if (!string.IsNullOrWhiteSpace(errors))
                {
                    if (result.Length > 0) result.AppendLine();
                    result.Append($"[stderr] {Truncate(errors, maxOutput / 4)}");
                }
                if (result.Length == 0)
                    result.Append($"(无输出，退出码={exitCode})");

                return new ToolResult
                {
                    Status = exitCode == 0 ? "success" : "failed",
                    Data = result.ToString(),
                    Error = exitCode != 0 ? $"退出码={exitCode}" : null
                };
            }
            catch (Exception ex)
            {
                return new ToolResult { Status = "failed", Error = $"SSH 执行异常: {ex.Message}" };
            }
        }

        private static string EscapeCommand(string command)
        {
            return "\"" + command.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string Truncate(string s, int maxLen)
        {
            s = s.TrimEnd();
            if (s.Length <= maxLen) return s;
            return s[..maxLen] + $"\n... (截断，共{s.Length}字符)";
        }
    }
}