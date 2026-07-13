using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    [ToolMeta(ContinueLoop = false, EngineTypes = new[] { "session" })]
    internal class DisableComponentTool : ITool
    {
        public string Name => "disable_component";
        public string Description => "卸载当前会话中的插件组件，释放其工具和资源";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("component_id", "要禁用的组件ID", 0)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var componentId = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            return Task.FromResult(new ToolResult { Status = "success", Data = $"Component {componentId} disabled (session-level)" });
        }
    }
}
