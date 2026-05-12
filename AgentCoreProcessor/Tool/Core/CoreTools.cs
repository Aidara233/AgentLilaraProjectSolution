using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Tool.Contract;

namespace AgentCoreProcessor.Tool.Core
{
    /// <summary>
    /// 循环继续信号。执行后触发下一轮循环。
    /// 当模型需要在非 ContinueLoop 工具操作后继续工作时使用。
    /// </summary>
    [ToolMeta(ContinueLoop = true)]
    internal class ContinueLoopTool : ITool
    {
        public string Name => "continue_loop";
        public string Description => "手动触发下一轮循环。当你需要在便签板等操作之后继续工作时使用";
        public IReadOnlyList<ToolParameter> Parameters => [];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            return Task.FromResult(new ToolResult { Status = "success" });
        }
    }

    /// <summary>
    /// 循环挂起信号。系统循环用来显式进入等待状态。
    /// </summary>
    [ToolMeta(ContinueLoop = false)]
    internal class WaitTool : ITool
    {
        public string Name => "wait";
        public string Description => "结束当前处理轮次，进入等待状态直到新事件到达";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("reason", "等待原因（简短说明为什么现在没事做）", 0),
            new("timeout_minutes", "等待超时时间（分钟），超时后自动唤醒。默认5", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var reason = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            return Task.FromResult(new ToolResult { Status = "success", Data = reason });
        }
    }
}
