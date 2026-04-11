using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 文件流写入器：将内容写入到指定文件中。
    /// inputs[0] = 要写入的内容（来自 ref 或 value）
    /// inputs[1] = 文件路径
    /// </summary>
    internal class FileWriteTool : ITool
    {
        public string Name => "文件流写入器";
        public string Description => "将内容写入到指定文件中";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("写入内容", "要写入的内容", 0, canBeRef: true), new("文件路径", "写入目标文件路径", 1)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 2)
                return new ToolResult { Status = "failed", Error = "缺少输入参数：需要写入内容和文件路径" };

            var content = resolvedInputs[0];
            var path = resolvedInputs[1];

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(path, content, ct);
            return new ToolResult { Status = "success", Data = $"已写入: {path}" };
        }
    }
}
