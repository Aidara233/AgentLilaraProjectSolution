using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 系统状态工具：切换系统循环的状态（Idle/Busy/DoNotDisturb）。
    /// 系统循环专用。
    /// </summary>
    internal class SystemStateTool : ITool
    {
        public string Name => "system_state";
        public string Description => "切换系统循环的状态（idle/busy/donotdisturb）";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("目标状态", "idle（空闲）/ busy（忙碌）/ donotdisturb（勿扰）", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "目标状态不能为空"
                });
            }

            var targetState = resolvedInputs[0].ToLower().Trim();
            var validStates = new[] { "idle", "busy", "donotdisturb" };

            if (!validStates.Contains(targetState))
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"无效的状态：{targetState}。有效值：idle / busy / donotdisturb"
                });
            }

            // 返回状态切换信号，由 SystemEngine 处理
            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = $"state:{targetState}"
            });
        }
    }
}
