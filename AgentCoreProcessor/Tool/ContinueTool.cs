using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 继续工具：手动触发下一轮循环。
    /// 当模型在同一轮中只调用了非 ContinueLoop 工具（如便签板）但还想继续操作时使用。
    /// </summary>
    internal class ContinueTool : ITool
    {
        public string Name => "continue_loop";
        public string Description => "手动触发下一轮循环。当你需要在便签板等操作之后继续工作时使用";
        public IReadOnlyList<ToolParameter> Parameters => [];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool ContinueLoop => true;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            return Task.FromResult(new ToolResult { Status = "success" });
        }
    }
}
