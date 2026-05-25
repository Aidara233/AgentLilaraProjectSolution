using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Component;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    /// <summary>
    /// 组件管理工具。模型可用此工具查看当前会话中的组件状态。
    /// 核心工具，始终可用，不属于任何组件。
    /// </summary>
    [ToolMeta(ContinueLoop = true, ExpressAvailable = true, CapabilitySummary = "查看当前会话的组件状态")]
    internal class ManageComponentsTool : ITool
    {
        /// <summary>引擎在执行工具前设置当前循环上下文。</summary>
        public static readonly AsyncLocal<LoopContext?> CurrentLoop = new();

        public record LoopContext(string LoopType);

        public string Name => "manage_components";
        public string Description => "查看当前会话的组件状态。可列出启用的组件列表。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("action", "操作：list（查看已启用的组件）", 0),
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var action = resolvedInputs.Count > 0 ? resolvedInputs[0]?.ToLower() : "list";

            var loop = CurrentLoop.Value;
            if (loop == null)
                return Task.FromResult(new ToolResult { Status = "failed", Error = "会话上下文未设置" });

            if (action != "list")
                return Task.FromResult(new ToolResult { Status = "failed", Error = $"未知操作: {action}，可用: list" });

            return Task.FromResult(ListComponents(loop));
        }

        private static ToolResult ListComponents(LoopContext loop)
        {
            var config = ComponentConfig.Load();
            var registrations = Component.ComponentRegistry.GetAll();
            var sb = new StringBuilder();

            sb.AppendLine("[已启用组件]");
            var enabledList = new List<string>();
            var disabledList = new List<string>();

            foreach (var reg in registrations)
            {
                var attr = ComponentAttribute.GetFrom(reg.Type);
                var name = attr?.Name ?? reg.Type.Name;
                if (config.IsEnabled(name, loop.LoopType, true))
                    enabledList.Add(name);
                else
                    disabledList.Add(name);
            }

            foreach (var c in enabledList)
                sb.AppendLine($"  ✓ {c}");

            if (disabledList.Count > 0)
            {
                sb.AppendLine("[已禁用组件]");
                foreach (var c in disabledList)
                    sb.AppendLine($"  ✗ {c}");
            }

            return new ToolResult { Status = "success", Data = sb.ToString() };
        }

        public JsonNode GetInputSchema() => JsonNode.Parse("""
        {
            "type": "object",
            "properties": {
                "action": { "type": "string", "enum": ["list"], "description": "操作类型" }
            },
            "required": ["action"]
        }
        """)!;
    }
}
