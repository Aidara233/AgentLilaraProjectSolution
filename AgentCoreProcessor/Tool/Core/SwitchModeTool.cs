using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool.Core
{
    /// <summary>
    /// 在 Working 子模式之间横向切换。不清空上下文，保留对话历史。
    /// 仅在 metaType == "Working" 的模式下可见。
    /// </summary>
    [ToolMeta(ContinueLoop = false, EngineTypes = new[] { "channel" })]
    internal class SwitchModeTool : ITool
    {
        public string Name => "switch_mode";
        public string Description => "在 Working 子模式之间切换（如 plan→build），保留上下文。不填 target 则列出可选模式。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("target", "目标模式 ID（留空查看可用模式列表）", 0),
            new("reason", "切换原因（可选）", 1)
        ];

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var modes = ModeConfigLoader.GetModesByMetaType("Working");
            var target = resolvedInputs.Count > 0 ? resolvedInputs[0]?.Trim() : null;

            if (string.IsNullOrEmpty(target))
            {
                var list = modes.Select(m => $"{m.Id} — {m.DisplayName}: {m.Description}").ToList();
                return Task.FromResult(new ToolResult
                {
                    Status = "success",
                    Data = "可用的 Working 子模式：\n" + string.Join("\n", list)
                });
            }

            var targetDef = modes.FirstOrDefault(m =>
                string.Equals(m.Id, target, StringComparison.OrdinalIgnoreCase));
            if (targetDef == null)
            {
                var available = string.Join(", ", modes.Select(m => m.Id));
                return Task.FromResult(new ToolResult
                {
                    Status = "error",
                    Error = $"未知模式 '{target}'。可选：{available}"
                });
            }

            var reason = resolvedInputs.Count > 1 ? resolvedInputs[1] : "";
            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = $"{target}|{reason}"
            });
        }
    }
}
