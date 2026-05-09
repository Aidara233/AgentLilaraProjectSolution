using System.Collections.Concurrent;
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
        private static readonly ConcurrentDictionary<string, ITool> _tools;
        private static readonly HashSet<string> _activeGroups = new();

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
                new RetainListTool(),
                new ActivateToolGroupTool(),
                new SelfKnowledgeTool(),
                new SendMediaTool(),
                // Phase 4: 系统循环工具（不需要 ISystemContext 的）
                new SystemStateTool(),
                new TaskQueueTool()
            };
            _tools = new ConcurrentDictionary<string, ITool>(toolList.ToDictionary(t => t.Name));
        }

        public static bool Register(ITool tool) => _tools.TryAdd(tool.Name, tool);

        public static bool Unregister(string toolName) => _tools.TryRemove(toolName, out _);

        public static ITool? Get(string toolName)
        {
            _tools.TryGetValue(toolName, out var tool);
            return tool;
        }

        public static IReadOnlyDictionary<string, ITool> All => _tools;

        /// <summary>激活工具组（会话级）。返回 false 表示组不存在。</summary>
        public static bool ActivateGroup(string groupName)
        {
            if (!_tools.Values.Any(t => t.ToolGroup == groupName)) return false;
            _activeGroups.Add(groupName);
            return true;
        }

        /// <summary>重置已激活的工具组。</summary>
        public static void ResetActiveGroups() => _activeGroups.Clear();

        /// <summary>
        /// 生成工具描述文本注入 prompt。
        /// 默认组和已展开组：完整描述。折叠组：一行摘要 + 提示使用「激活工具组」。
        /// </summary>
        public static string GenerateDescriptions(
            IEnumerable<ITool>? tools = null,
            HashSet<string>? authorizedTools = null,
            Func<ITool, bool>? filter = null)
        {
            var source = (tools ?? _tools.Values).ToList();
            if (filter != null) source = source.Where(filter).ToList();
            var sb = new StringBuilder();
            int i = 1;

            // 默认组（ToolGroup == null）+ 已展开组：完整描述
            var expanded = source.Where(t =>
                t.ToolGroup == null || t.DefaultExpanded || _activeGroups.Contains(t.ToolGroup!));
            foreach (var tool in expanded)
            {
                bool isRestricted = tool.RequiredPermission > PermissionLevel.Default;
                bool isAuthorized = authorizedTools != null && authorizedTools.Contains(tool.Name);
                var suffix = isRestricted && !isAuthorized ? "（需要管理员授权）" : "";

                sb.AppendLine($"工具{i}：{tool.Name}{suffix}");
                sb.AppendLine($"描述：{tool.Description}");
                if (tool.Parameters.Count > 0)
                {
                    var paramParts = tool.Parameters.Select(p => $"inputs[{p.Index}] = {p.Name}");
                    sb.AppendLine($"参数：{string.Join(", ", paramParts)}");
                }
                sb.AppendLine($"示例：{GenerateExample(tool)}<over>");
                sb.AppendLine();
                i++;
            }

            // 折叠组：摘要
            var collapsedGroups = source
                .Where(t => t.ToolGroup != null && !t.DefaultExpanded && !_activeGroups.Contains(t.ToolGroup!))
                .GroupBy(t => t.ToolGroup!)
                .ToList();

            if (collapsedGroups.Count > 0)
            {
                sb.AppendLine("[可用工具组]（使用「激活工具组」展开详细描述）");
                foreach (var group in collapsedGroups)
                {
                    var names = string.Join("、", group.Select(t => t.Name));
                    sb.AppendLine($"- {group.Key}：{names}");
                }
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 生成能力摘要列表，注入 Express prompt 让模型知道可升级到 Working 模式的能力。
        /// </summary>
        public static string GenerateCapabilitySummary(Func<ITool, bool>? filter = null)
        {
            var source = filter != null
                ? _tools.Values.Where(filter)
                : _tools.Values;
            var capabilities = source
                .Where(t => t.CapabilitySummary != null)
                .Select(t => t.CapabilitySummary!)
                .Distinct()
                .ToList();

            if (capabilities.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("你当前处于轻量对话模式。如果对话涉及以下任何能力，输出 [ESCALATE]你要做什么 切换到工作模式：");
            foreach (var cap in capabilities)
                sb.AppendLine($"- {cap}");
            sb.AppendLine("- 任何需要实际操作而非纯对话的场景");
            sb.AppendLine("注意：[ESCALATE]后面写上你打算做什么，例如 [ESCALATE]帮你查一下记忆里有没有相关内容");
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
