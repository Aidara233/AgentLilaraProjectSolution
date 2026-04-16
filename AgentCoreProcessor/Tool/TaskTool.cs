using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 任务管理工具：维护主线任务列表，防止多轮循环中跑偏。
    /// 纯信号工具——参数验证后返回操作描述，实际列表操作由 WorkingCore 处理。
    /// </summary>
    internal class TaskTool : ITool
    {
        public string Name => "任务管理";
        public string Description => "管理主线任务列表。add 添加任务，complete 标记完成（按序号），remove 移除任务（按序号）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("操作", "add / complete / remove", 0),
            new("内容", "add 时为任务描述，complete/remove 时为任务序号（从1开始）", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 2)
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "需要操作和内容两个参数"
                });

            var action = resolvedInputs[0].Trim().ToLower();
            var content = resolvedInputs[1].Trim();

            switch (action)
            {
                case "add":
                    if (string.IsNullOrEmpty(content))
                        return Task.FromResult(new ToolResult
                        {
                            Status = "failed",
                            Error = "任务描述不能为空"
                        });
                    return Task.FromResult(new ToolResult
                    {
                        Status = "success",
                        Data = $"add:{content}"
                    });

                case "complete":
                case "remove":
                    if (!int.TryParse(content, out var index) || index < 1)
                        return Task.FromResult(new ToolResult
                        {
                            Status = "failed",
                            Error = "序号必须是大于0的整数"
                        });
                    return Task.FromResult(new ToolResult
                    {
                        Status = "success",
                        Data = $"{action}:{index}"
                    });

                default:
                    return Task.FromResult(new ToolResult
                    {
                        Status = "failed",
                        Error = $"未知操作: {action}，支持 add / complete / remove"
                    });
            }
        }
    }
}
