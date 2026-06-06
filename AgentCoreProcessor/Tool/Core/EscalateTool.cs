using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    [ToolMeta(ContinueLoop = false, EngineTypes = new[] { "channel" })]
    internal class EscalateTool : ITool
    {
        public string Name => "escalate";
        public string Description => "切换到工作模式以执行实际操作（查记忆、管理文件、委托任务等）。需要做事时调用。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("reason", "切换原因（简述你打算做什么）", 0)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var reason = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            return Task.FromResult(new ToolResult { Status = "success", Data = reason });
        }
    }
}
