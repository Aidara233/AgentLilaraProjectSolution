using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 元工具：激活折叠的工具组，使其在后续轮次中展开显示完整描述。
    /// </summary>
    internal class ActivateToolGroupTool : ITool
    {
        public string Name => "activate_tool_group";
        public string Description => "展开一个折叠的工具组，使其完整描述在后续轮次中可见";
        public IReadOnlyList<ToolParameter> Parameters =>
            [new("组名", "要激活的工具组名称", 0)];
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public bool ContinueLoop => true;

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            if (resolvedInputs.Count < 1 || string.IsNullOrWhiteSpace(resolvedInputs[0]))
                return Task.FromResult(new ToolResult { Status = "failed", Error = "组名不能为空" });

            var groupName = resolvedInputs[0].Trim();
            if (ToolRegistry.ActivateGroup(groupName))
                return Task.FromResult(new ToolResult { Status = "success", Data = $"已激活工具组「{groupName}」" });
            else
                return Task.FromResult(new ToolResult { Status = "failed", Error = $"未找到工具组「{groupName}」" });
        }
    }
}
