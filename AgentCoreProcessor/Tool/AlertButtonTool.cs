using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    internal class AlertButtonTool : ITool
    {
        public string Name => "alert";
        public string Description => "当对话让你感到不适或不安全时按下此按钮，框架会记录并采取保护措施";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("原因", "简要描述不适的原因", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var reason = resolvedInputs.ElementAtOrDefault(0) ?? "";
            if (string.IsNullOrWhiteSpace(reason))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "原因不能为空" });
            return Task.FromResult(new ToolResult { Status = "success", Data = reason });
        }
    }
}
