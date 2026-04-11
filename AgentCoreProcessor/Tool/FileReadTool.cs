using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 文件流读取器：将指定文件的内容读取并返回。
    /// inputs[0] = 文件路径
    /// </summary>
    internal class FileReadTool : ITool
    {
        public string Name => "文件流读取器";
        public string Description => "将指定文件的内容读取并返回";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("文件路径", "要读取的文件完整路径", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1)
                return new ToolResult { Status = "failed", Error = "缺少输入参数：文件路径" };

            var path = resolvedInputs[0];

            if (!File.Exists(path))
                return new ToolResult { Status = "failed", Error = $"文件不存在: {path}" };

            var content = await File.ReadAllTextAsync(path, ct);
            return new ToolResult { Status = "success", Data = content };
        }
    }
}
