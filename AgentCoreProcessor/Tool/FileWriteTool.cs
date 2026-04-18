using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Tool
{
    internal class FileWriteTool : ITool
    {
        public string Name => "写入文件";
        public string Description => "向 Storage 目录内的文件写入内容。路径可以是相对于 Storage/ 的相对路径或绝对路径。支持覆盖写和追加模式。Storage/Workspace/ 是自由工作区";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("文件路径", "要写入的文件路径", 0),
            new("内容", "要写入的文本内容", 1),
            new("模式", "overwrite（覆盖，默认）或 append（追加）", 2)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);
        public PermissionLevel RequiredPermission => PermissionLevel.Elevated;
        public bool ContinueLoop => true;
        public string? CapabilitySummary => "写入或修改文件";

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var rawPath = resolvedInputs.ElementAtOrDefault(0) ?? "";
            if (string.IsNullOrWhiteSpace(rawPath))
                return new ToolResult { Status = "failed", Error = "文件路径不能为空" };

            var content = resolvedInputs.ElementAtOrDefault(1) ?? "";
            var mode = (resolvedInputs.ElementAtOrDefault(2) ?? "overwrite").Trim().ToLower();
            bool append = mode == "append";

            var fullPath = FileAccessControl.ResolvePath(rawPath);
            var (allowed, error) = FileAccessControl.CheckAccess(fullPath);
            if (!allowed)
                return new ToolResult { Status = "failed", Error = error };

            var (sizeOk, sizeError) = FileAccessControl.CheckWriteSize(content);
            if (!sizeOk)
                return new ToolResult { Status = "failed", Error = sizeError };

            if (FileAccessControl.IsWorkspacePath(fullPath))
            {
                long additionalBytes = content.Length * 2L;
                var (capOk, capError) = FileAccessControl.CheckWorkspaceCapacity(additionalBytes);
                if (!capOk)
                    return new ToolResult { Status = "failed", Error = capError };
            }

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (append)
                await File.AppendAllTextAsync(fullPath, content, ct);
            else
                await File.WriteAllTextAsync(fullPath, content, ct);

            var info = new FileInfo(fullPath);
            return new ToolResult
            {
                Status = "success",
                Data = $"已{(append ? "追加" : "写入")}: {rawPath} ({info.Length}字节)"
            };
        }
    }
}
