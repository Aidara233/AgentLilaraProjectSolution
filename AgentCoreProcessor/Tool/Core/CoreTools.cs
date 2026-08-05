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
    /// 可带 journal 参数随手记当下经历（走心智引擎 Writer，L2/L3 + 情绪信号）。
    /// </summary>
    [ToolMeta(ContinueLoop = false)]
    internal class WaitTool : ITool
    {
        public string Name => "wait";
        public string Description => "挂起当前循环、等待用户下一句话（会话保持，用户回应即唤醒）。说完话、暂时没事做时的默认收尾。可带 journal 随手记当下经历。注意：这只是挂起等待，不结束会话；要退出工作模式请用 end_session。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("reason", "等待原因（简短说明为什么现在停下）", 0),
            new("timeout_minutes", "（可选）等待超时分钟数，超时后自动唤醒，默认5", 1, isRequired: false),
            new("journal", "（可选）随手记的经历（第一人称正文，可带 [情绪:轴:目标值] 标记）", 2, isRequired: false)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var reason = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            return Task.FromResult(new ToolResult { Status = "success", Data = reason });
        }
    }
}
