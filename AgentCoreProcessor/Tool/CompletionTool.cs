using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 完成工具：标记任务完成，终止 Agent 循环。
    /// 纯信号工具——本身不执行副作用，由 Agent 循环检测后退出。
    /// </summary>
    internal class CompletionTool : ITool
    {
        public string Name => "完成";
        public string Description => "标记任务完成，终止当前处理循环";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("完成摘要", "对本次任务执行结果的简短总结（可选）", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            // 摘要可选，没有也行
            var summary = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = summary
            });
        }
    }
}
