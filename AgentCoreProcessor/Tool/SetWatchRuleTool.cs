using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 设置关注规则工具。允许系统循环为频道会话配置关注规则。
    /// Phase 6 实现：直接操作 ChannelEngine 的 watchRules 字段。
    /// </summary>
    internal class SetWatchRuleTool : ITool
    {
        private ISystemContext? ctx;

        public string Name => "set_watch_rule";
        public string Description =>
            "为指定频道设置关注规则。当频道中出现匹配的消息时，根据规则执行相应动作（通知/打断/升级）。";
        public string? CapabilitySummary => null;
        public PermissionLevel RequiredPermission => PermissionLevel.Default;
        public bool ContinueLoop => false;
        public string? ToolGroup => null;
        public bool DefaultExpanded => true;
        public TimeSpan Timeout => TimeSpan.FromSeconds(10);

        public IReadOnlyList<ToolParameter> Parameters => new List<ToolParameter>
        {
            new ToolParameter("channelId", "频道 ID（整数）", 0),
            new ToolParameter("ruleId", "规则 ID（字符串，用于后续更新/删除）", 1),
            new ToolParameter("description", "规则描述", 2),
            new ToolParameter("pattern", "匹配模式（关键词或正则表达式）", 3),
            new ToolParameter("action", "动作：notify（通知）/interrupt（打断）/escalate（升级）", 4),
            new ToolParameter("autoResponse", "是否自动回应（true/false，可选，默认 false）", 5)
        };

        /// <summary>设置系统上下文（由 MasterEngine 在注册时调用）。</summary>
        public void SetContext(ISystemContext context)
        {
            this.ctx = context;
        }

        public Task<ToolResult> ExecuteAsync(List<string> inputs, CancellationToken ct)
        {
            if (ctx == null)
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "[错误] 工具未初始化系统上下文"
                });

            if (inputs.Count < 5)
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = "[错误] 参数不足，需要：channelId, ruleId, description, pattern, action"
                });

            // 解析参数
            if (!int.TryParse(inputs[0], out int channelId))
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"[错误] 无效的频道 ID: {inputs[0]}"
                });

            string ruleId = inputs[1];
            string description = inputs[2];
            string pattern = inputs[3];
            string actionStr = inputs[4].ToLower();

            WatchAction action;
            switch (actionStr)
            {
                case "notify":
                    action = WatchAction.Notify;
                    break;
                case "interrupt":
                    action = WatchAction.Interrupt;
                    break;
                case "escalate":
                    action = WatchAction.Escalate;
                    break;
                default:
                    return Task.FromResult(new ToolResult
                    {
                        Status = "failed",
                        Error = $"[错误] 无效的动作: {actionStr}，必须是 notify/interrupt/escalate"
                    });
            }

            bool autoResponse = false;
            if (inputs.Count > 5 && !string.IsNullOrEmpty(inputs[5]))
            {
                if (!bool.TryParse(inputs[5], out autoResponse))
                    return Task.FromResult(new ToolResult
                    {
                        Status = "failed",
                        Error = $"[错误] 无效的 autoResponse 值: {inputs[5]}"
                    });
            }

            // 创建规则
            var rule = new WatchRule
            {
                RuleId = ruleId,
                Description = description,
                Pattern = pattern,
                Action = action,
                AutoResponse = autoResponse
            };

            // 查找目标频道的 ChannelEngine
            var engines = ctx.GetActiveEnginesSnapshot();
            var worker = engines.OfType<ChannelEngine>()
                .FirstOrDefault(w => w.ChannelId == channelId && w.IsAlive);

            if (worker == null)
            {
                return Task.FromResult(new ToolResult
                {
                    Status = "failed",
                    Error = $"[错误] 频道 {channelId} 没有活跃的 Worker 引擎"
                });
            }

            // 更新规则
            var existingRules = worker.GetWatchRules();
            var updatedRules = existingRules
                .Where(r => r.RuleId != ruleId)
                .Append(rule)
                .ToList();

            worker.UpdateWatchRules(updatedRules);

            FrameworkLogger.Log("SetWatchRuleTool",
                $"已设置关注规则: channelId={channelId}, ruleId={ruleId}, action={action}");

            var result = $"[成功] 已为频道 {channelId} 设置关注规则「{ruleId}」\n" +
                   $"描述：{description}\n" +
                   $"模式：{pattern}\n" +
                   $"动作：{action}\n" +
                   $"自动回应：{autoResponse}";

            return Task.FromResult(new ToolResult
            {
                Status = "success",
                Data = result
            });
        }
    }
}

