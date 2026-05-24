using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    [ToolMeta(ContinueLoop = false)]
    internal class DeescalateTool : ITool
    {
        public string Name => "deescalate";
        public string Description => "切换回轻量对话模式（Express）。工作完成后调用，释放资源并结束工作会话。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("reason", "切换原因（简述完成了什么，可选）", 0)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var reason = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            return Task.FromResult(new ToolResult { Status = "success", Data = reason });
        }
    }
}
