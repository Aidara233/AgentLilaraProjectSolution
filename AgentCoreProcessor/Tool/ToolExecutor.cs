using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具执行器。顺序执行工具调用列表，支持授权检查。
    /// </summary>
    internal class ToolExecutor
    {
        private readonly Func<string, ITool?> toolResolver;
        private readonly HashSet<string>? authorizedTools;

        /// <summary>
        /// 授权回调：工具名 + 所需权限 → 是否通过。
        /// 由 WorkerEngine 注入，内部走验证码流程。null 时未授权直接拒绝。
        /// </summary>
        public Func<string, PermissionLevel, Task<bool>>? OnAuthRequired { get; set; }

        /// <summary>
        /// 每个工具执行完毕后立即触发的回调。用于即时处理副作用（如发送说话消息），
        /// 避免被后续工具的授权流程阻塞。
        /// </summary>
        public Func<ToolCall, ToolResult, Task>? OnToolExecuted { get; set; }

        public ToolExecutor(
            Func<string, ITool?>? toolResolver = null,
            HashSet<string>? authorizedTools = null)
        {
            this.toolResolver = toolResolver ?? ToolRegistry.Get;
            this.authorizedTools = authorizedTools;
        }

        public async Task<List<ToolResult>> ExecuteAsync(List<ToolCall> calls)
        {
            var results = new List<ToolResult>();
            foreach (var call in calls)
            {
                var result = await RunSingleAsync(call);
                results.Add(result);
                if (OnToolExecuted != null)
                    await OnToolExecuted(call, result);
            }
            return results;
        }

        private async Task<ToolResult> RunSingleAsync(ToolCall call)
        {
            var tool = toolResolver(call.Tool);
            if (tool == null)
                return new ToolResult { Status = "failed", Error = $"未知工具: {call.Tool}" };

            // 授权检查：未授权时触发回调（框架透明授权）
            if (authorizedTools != null
                && tool.RequiredPermission > PermissionLevel.Default
                && !authorizedTools.Contains(tool.Name))
            {
                if (OnAuthRequired != null)
                {
                    var approved = await OnAuthRequired(tool.Name, tool.RequiredPermission);
                    if (approved)
                        authorizedTools.Add(tool.Name);
                    else
                        return new ToolResult { Status = "failed", Error = "授权被拒绝" };
                }
                else
                {
                    return new ToolResult { Status = "failed", Error = "未授权" };
                }
            }

            using var cts = new CancellationTokenSource(tool.Timeout);
            try
            {
                return await tool.ExecuteAsync(call.Inputs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return new ToolResult { Status = "failed", Error = $"执行超时（{tool.Timeout.TotalSeconds}s）" };
            }
            catch (Exception ex)
            {
                return new ToolResult { Status = "failed", Error = ex.Message };
            }
        }
    }
}