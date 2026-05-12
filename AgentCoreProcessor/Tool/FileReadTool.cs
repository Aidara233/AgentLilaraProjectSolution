using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    internal class FileReadTool : ITool
    {
        public string Name => "read_file";
        public string Description => "读取 Storage 目录内的文件内容。路径可以是相对于 Storage/ 的相对路径或绝对路径。Storage/Workspace/ 是自由工作区";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("文件路径", "要读取的文件路径", 0),
            new("最大字符数", "返回内容的最大字符数，默认4000", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public bool ContinueLoop => true;
        public string? ToolGroup => "文件操作";
        public bool RetainResult => true;
        public string? CapabilitySummary => "读取文件内容";

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var rawPath = resolvedInputs.ElementAtOrDefault(0) ?? "";
            if (string.IsNullOrWhiteSpace(rawPath))
                return new ToolResult { Status = "failed", Error = "文件路径不能为空" };

            int maxChars = 4000;
            var maxStr = resolvedInputs.ElementAtOrDefault(1);
            if (!string.IsNullOrWhiteSpace(maxStr) && int.TryParse(maxStr, out var parsed))
                maxChars = Math.Clamp(parsed, 100, 20000);

            var fullPath = FileAccessControl.ResolvePath(rawPath);
            var (allowed, error) = FileAccessControl.CheckAccess(fullPath);
            if (!allowed)
                return new ToolResult { Status = "failed", Error = error };

            if (!File.Exists(fullPath))
                return new ToolResult { Status = "failed", Error = $"文件不存在: {rawPath}" };

            var bytes = await File.ReadAllBytesAsync(fullPath, ct);
            if (bytes.Length > 0 && Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 512)) >= 0)
            {
                var ext = Path.GetExtension(fullPath).ToLower();
                return new ToolResult
                {
                    Status = "success",
                    Data = $"[二进制文件] 大小={bytes.Length}字节, 类型={ext}"
                };
            }

            var content = Encoding.UTF8.GetString(bytes);
            if (content.Length > maxChars)
                content = content[..maxChars] + $"\n... (截断，共{content.Length}字符)";

            return new ToolResult { Status = "success", Data = content };
        }
    }
}
