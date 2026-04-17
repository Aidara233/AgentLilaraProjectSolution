using System.Collections.Generic;
using System.Linq;
using System.Text;
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
                // FileReadTool / FileWriteTool 暂禁用：无路径沙盒，群聊场景风险过高
                new SpeakTool(),
                new CompletionTool(),
                new ThinkingNotesTool(),
                new MemoryTool(),
                new DreamPermissionTool(),
                new ForceSleepTool(),
                new DreamConfigTool(),
                new SleepScoreTool(),
                new RedAlertTool(),
                new ReviewHintTool(),
                // DelegateTool / SubAgentDetailTool 暂禁用：子 agent 会继承文件工具风险
                new TaskTool(),
                new AlertButtonTool()
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
        /// 根据已注册工具的元数据，自动生成供 prompt 注入的工具描述文本。
        /// 传入 tools 时只生成指定工具的描述（Phase 2 子 agent 用）；
        /// 不传时生成全部工具的描述。
        /// </summary>
        public static string GenerateDescriptions(IEnumerable<ITool>? tools = null)
        {
            var source = tools ?? _tools.Values;
            var sb = new StringBuilder();
            int i = 1;

            foreach (var tool in source)
            {
                sb.AppendLine($"工具{i}：{tool.Name}");
                sb.AppendLine($"描述：{tool.Description}");

                // 参数说明
                if (tool.Parameters.Count > 0)
                {
                    var paramParts = tool.Parameters
                        .Select(p =>
                        {
                            var typeHint = p.CanBeRef ? "value 或 ref" : "value";
                            return $"inputs[{p.Index}] = {p.Name} ({typeHint})";
                        });
                    sb.AppendLine($"参数：{string.Join(", ", paramParts)}");
                }

                // 自动生成示例 JSON
                sb.AppendLine($"示例：{GenerateExample(tool)}<over>");
                sb.AppendLine();
                i++;
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 为工具自动生成一条示例 ToolCall JSON。
        /// </summary>
        private static string GenerateExample(ITool tool)
        {
            var example = new
            {
                tool = tool.Name,
                toolId = $"example{tool.Name.GetHashCode():x}",
                inputs = tool.Parameters.Select(p => new
                {
                    type = "value",
                    value = $"({p.Name})"
                }),
                output = "result",
                outputToModel = false,
                retain = false
            };
            return JsonConvert.SerializeObject(example, Formatting.None);
        }
    }
}
