using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    [ToolMeta(ContinueLoop = false, EngineTypes = new[] { "channel" })]
    internal class EnableComponentTool : ITool
    {
        public string Name => "enable_component";
        public string Description => "动态加载已安装的插件组件，使其工具立即可用";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("component_id", "要启用的组件ID", 0)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var componentId = resolvedInputs.Count > 0 ? resolvedInputs[0] : "";
            return Task.FromResult(new ToolResult { Status = "success", Data = $"Component {componentId} enabled (session-level)" });
        }
    }
}
