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
    /// 在 Working 子模式之间横向切换。保留上下文，不清空对话历史。
    /// 仅在 metaType == "Working" 的模式下可见。
    /// </summary>
    [ToolMeta(ContinueLoop = false, EngineTypes = new[] { "channel" })]
    internal class SwitchModeTool : ITool
    {
        public string Name => "switch_mode";
        public string Description =>
            "在 Working 子模式之间横向切换（如 plan→build），保留上下文。" +
            "不填 target 则列出可选模式。\n\n" +
            "【必须用户确认】除非用户消息中直接要求切换（此时 asked_user_message 留空），" +
            "否则你必须先用 speak 征求用户同意，等到用户明确回复同意后，将你询问的内容填入 asked_user_message，" +
            "将用户确认消息的 platform_id 填入 user_confirm_message_id。";

        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("target", "目标模式 ID（留空则返回可用模式列表）", 0),
            new("reason", "切换原因，简述为什么需要切到此模式", 1),
            new("asked_user_message", "你征求用户同意时发送的消息内容。如果用户直接要求切换则留空", 2, isRequired: false),
            new("user_confirm_message_id", "用户确认/要求切换的那条消息的 platform_id（db_id 也可）", 3)
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
            var askedMsg = resolvedInputs.Count > 2 ? resolvedInputs[2] : "";
            var confirmMsgId = resolvedInputs.Count > 3 ? resolvedInputs[3] : "";

            // 用 | 分隔四个字段，ChannelEngine 解析
            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = $"{target}|{reason}|{askedMsg}|{confirmMsgId}"
            });
        }
    }
}
