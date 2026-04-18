using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgentCoreProcessor.Database;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具注册表。管理所有可用工具，并可动态生成工具描述注入 prompt。
    /// </summary>
    internal static class ToolRegistry
    {
        private static readonly Dictionary<string, ITool> _tools;

        static ToolRegistry()
        {
            var toolList = new ITool[]
            {
                new FileReadTool(),
                new FileWriteTool(),
                new FileManagementTool(),
                new SpeakTool(),
                new ThinkingNotesTool(),
                new MemoryTool(),
                new DreamPermissionTool(),
                new ForceSleepTool(),
                new DreamConfigTool(),
                new SleepScoreTool(),
                new RedAlertTool(),
                new ReviewHintTool(),
                new TaskTool(),
                new AlertButtonTool(),
                new RemoteShellTool(),
                new FileTransferTool(),
                new ContinueTool(),
                new PinboardTool(),
                new RetainListTool()
            };
            _tools = toolList.ToDictionary(t => t.Name);
        }

        public static ITool? Get(string toolName)
        {
            _tools.TryGetValue(toolName, out var tool);
            return tool;
        }

        public static IReadOnlyDictionary<string, ITool> All => _tools;

        /// <summary>
        /// 生成工具描述文本注入 prompt。
        /// 受限工具标注"（首次使用需要确认）"，但显示完整描述。
        /// </summary>
        public static string GenerateDescriptions(
            IEnumerable<ITool>? tools = null,
            HashSet<string>? authorizedTools = null)
        {
            var source = tools ?? _tools.Values;
            var sb = new StringBuilder();
            int i = 1;

            foreach (var tool in source)
            {
                bool isRestricted = tool.RequiredPermission > PermissionLevel.Default;
                bool isAuthorized = authorizedTools != null && authorizedTools.Contains(tool.Name);

                var suffix = isRestricted && !isAuthorized ? "（首次使用需要确认）" : "";

                sb.AppendLine($"工具{i}：{tool.Name}{suffix}");
                sb.AppendLine($"描述：{tool.Description}");

                if (tool.Parameters.Count > 0)
                {
                    var paramParts = tool.Parameters
                        .Select(p => $"inputs[{p.Index}] = {p.Name}");
                    sb.AppendLine($"参数：{string.Join(", ", paramParts)}");
                }

                sb.AppendLine($"示例：{GenerateExample(tool)}<over>");
                sb.AppendLine();
                i++;
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 生成能力摘要列表，注入 Express prompt 让模型知道可升级到 Working 模式的能力。
        /// </summary>
        public static string GenerateCapabilitySummary()
        {
            var capabilities = _tools.Values
                .Where(t => t.CapabilitySummary != null)
                .Select(t => t.CapabilitySummary!)
                .Distinct()
                .ToList();

            if (capabilities.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("你当前处于轻量对话模式。如果对话涉及以下任何能力，输出 [ESCALATE] 切换到工作模式：");
            foreach (var cap in capabilities)
                sb.AppendLine($"- {cap}");
            sb.AppendLine("- 任何需要实际操作而非纯对话的场景");
            return sb.ToString().TrimEnd();
        }

        private static string GenerateExample(ITool tool)
        {
            var example = new
            {
                tool = tool.Name,
                inputs = tool.Parameters.Select(p => $"({p.Name})").ToArray()
            };
            return JsonConvert.SerializeObject(example, Formatting.None);
        }
    }
}
