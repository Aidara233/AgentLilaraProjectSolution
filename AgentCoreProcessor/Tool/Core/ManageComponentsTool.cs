using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    /// <summary>
    /// 组件管理工具。模型可用此工具查看、激活、停用当前会话中的组件。
    /// 核心工具，始终可用，不属于任何组件。
    /// </summary>
    [ToolMeta(ContinueLoop = true, ExpressAvailable = true, CapabilitySummary = "管理当前会话的组件（启用/禁用可用插件）")]
    internal class ManageComponentsTool : ITool
    {
        private readonly ToolProfileManager _profiles;

        /// <summary>引擎在执行工具前设置当前循环上下文。</summary>
        public static readonly AsyncLocal<LoopContext?> CurrentLoop = new();

        public record LoopContext(string ProfileName, string SessionId);

        public ManageComponentsTool(ToolProfileManager profiles)
        {
            _profiles = profiles;
        }

        public string Name => "manage_components";
        public string Description => "管理当前会话的组件。可查看可用组件列表、激活或停用组件。激活后组件的工具将可用。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("action", "操作：list（查看）/ activate（激活）/ deactivate（停用）", 0),
            new("component", "组件名（activate/deactivate 时必填）", 1, isRequired: false)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var action = resolvedInputs.Count > 0 ? resolvedInputs[0]?.ToLower() : "list";
            var component = resolvedInputs.Count > 1 ? resolvedInputs[1] : null;

            var loop = CurrentLoop.Value;
            if (loop == null)
                return Task.FromResult(new ToolResult { Status = "failed", Error = "会话上下文未设置" });

            switch (action)
            {
                case "list":
                    return Task.FromResult(ListComponents(loop));
                case "activate":
                    return Task.FromResult(Activate(loop, component));
                case "deactivate":
                    return Task.FromResult(Deactivate(loop, component));
                default:
                    return Task.FromResult(new ToolResult { Status = "failed", Error = $"未知操作: {action}，可用: list/activate/deactivate" });
            }
        }

        private ToolResult ListComponents(LoopContext loop)
        {
            var sb = new StringBuilder();
            var active = _profiles.GetActiveComponents(loop.ProfileName, loop.SessionId);
            var activatable = _profiles.GetActivatableComponents(loop.ProfileName, loop.SessionId);

            sb.AppendLine("[当前活跃组件]");
            foreach (var c in active)
                sb.AppendLine($"  ✓ {c}");

            if (activatable.Count > 0)
            {
                sb.AppendLine("[可激活组件（当前未启用）]");
                foreach (var c in activatable)
                    sb.AppendLine($"  ○ {c}");
            }

            return new ToolResult { Status = "success", Data = sb.ToString() };
        }

        private ToolResult Activate(LoopContext loop, string? component)
        {
            if (string.IsNullOrWhiteSpace(component))
                return new ToolResult { Status = "failed", Error = "component 参数不能为空" };

            if (_profiles.ActivateComponent(loop.ProfileName, loop.SessionId, component))
                return new ToolResult { Status = "success", Data = $"组件 '{component}' 已激活，其工具现在可用。" };

            var state = _profiles.GetComponentState(loop.ProfileName, component);
            return state switch
            {
                ComponentState.Unavailable => new ToolResult { Status = "failed", Error = $"组件 '{component}' 在当前配置中不可用" },
                ComponentState.Enabled => new ToolResult { Status = "failed", Error = $"组件 '{component}' 已经是启用状态" },
                _ => new ToolResult { Status = "failed", Error = $"无法激活组件 '{component}'" }
            };
        }

        private ToolResult Deactivate(LoopContext loop, string? component)
        {
            if (string.IsNullOrWhiteSpace(component))
                return new ToolResult { Status = "failed", Error = "component 参数不能为空" };

            if (_profiles.DeactivateComponent(loop.ProfileName, loop.SessionId, component))
                return new ToolResult { Status = "success", Data = $"组件 '{component}' 已停用。" };

            return new ToolResult { Status = "failed", Error = $"组件 '{component}' 未处于手动激活状态，无法停用" };
        }

        public JsonNode GetInputSchema() => JsonNode.Parse("""
        {
            "type": "object",
            "properties": {
                "action": { "type": "string", "enum": ["list", "activate", "deactivate"], "description": "操作类型" },
                "component": { "type": "string", "description": "组件名（activate/deactivate 时必填）" }
            },
            "required": ["action"]
        }
        """)!;
    }
}
