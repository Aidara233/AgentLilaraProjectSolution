using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

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
    /// 循环挂起信号。Working 模式下显式停止循环。不调用则默认继续下一轮。
    /// </summary>
    [ToolMeta(ContinueLoop = false, EngineTypes = new[] { "channel", "system" })]
    internal class WaitTool : ITool
    {
        public string Name => "wait";
        public string Description => "停止当前工作循环，进入等待状态。工作模式默认每轮自动继续，只在任务完成或需要等待用户回应时调用此工具。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("reason", "等待原因（简短说明为什么现在停下）", 0),
            new("timeout_minutes", "（可选）等待超时分钟数，超时后自动唤醒，默认5", 1, isRequired: false)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var reason = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            return Task.FromResult(new ToolResult { Status = "success", Data = reason });
        }
    }
}
