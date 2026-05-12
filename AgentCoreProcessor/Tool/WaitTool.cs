using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 等待工具（系统循环专用）。
    /// 模型调用后 Agent 循环 break，闸门落下等待下一次唤醒。
    /// </summary>
    internal class WaitTool : ITool
    {
        public string Name => "wait";
        public string Description => "结束当前处理轮次，进入等待状态直到新事件到达。当你判断当前没有需要处理的事务时调用";
        public IReadOnlyList<ToolParameter> Parameters => new[]
        {
            new ToolParameter("reason", "等待原因（简短说明为什么现在没事做）", 0),
            new ToolParameter("timeout_minutes", "等待超时时间（分钟），超时后自动唤醒检查。默认5", 1)
        };
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool ContinueLoop => false;
        public bool AllowSubAgent => false;

        public string? WaitReason { get; private set; }
        public int TimeoutMinutes { get; private set; } = 5;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            WaitReason = resolvedInputs.Count > 0 ? resolvedInputs[0] : "无事可做";
            if (resolvedInputs.Count > 1 && int.TryParse(resolvedInputs[1], out var mins))
                TimeoutMinutes = Math.Clamp(mins, 1, 60);

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = $"进入等待状态，原因: {WaitReason}，超时: {TimeoutMinutes}分钟"
            });
        }
    }
}
