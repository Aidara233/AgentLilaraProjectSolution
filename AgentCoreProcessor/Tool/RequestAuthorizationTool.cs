using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    internal class RequestAuthorizationTool : ITool
    {
        public string Name => "申请工具授权";
        public string Description => "申请使用受限工具的权限。框架会向用户发送验证码，有权限的用户确认后解锁";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("工具名列表", "要申请的工具名，多个用逗号分隔", 0),
            new("申请理由", "简要说明为什么需要使用这些工具", 1)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool AllowSubAgent => false;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var toolNames = resolvedInputs.ElementAtOrDefault(0) ?? "";
            var reason = resolvedInputs.ElementAtOrDefault(1) ?? "";

            if (string.IsNullOrWhiteSpace(toolNames))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "工具名列表不能为空" });
            if (string.IsNullOrWhiteSpace(reason))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "申请理由不能为空" });

            var names = toolNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var invalid = names.Where(n => ToolRegistry.Get(n) == null).ToList();
            if (invalid.Count > 0)
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"未知工具: {string.Join(", ", invalid)}"
                });

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = $"{string.Join(",", names)}|{reason}"
            });
        }
    }
}
