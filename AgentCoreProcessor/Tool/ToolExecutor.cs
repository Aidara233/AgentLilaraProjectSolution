using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Modules;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具执行器。顺序执行工具调用列表，支持禁用检查和权限检查。
    /// </summary>
    internal class ToolExecutor
    {
        private readonly Func<string, ITool?> toolResolver;

        /// <summary>当前权限组（express/channel/task/review）。设置后执行期硬校验：组外工具返回 failed。</summary>
        public string? PermissionGroup { get; set; }

        /// <summary>每个工具执行完毕后触发的回调。</summary>
        public Func<ToolCall, ToolResult, Task>? OnToolExecuted { get; set; }

        public ToolExecutor(
            Func<string, ITool?>? toolResolver = null)
        {
            this.toolResolver = toolResolver ?? ToolRegistry.Get;
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
            {
                return new ToolResult { Status = "failed", Error = $"未知工具: {call.Tool}" };
            }

            if (ToolRegistry.IsDisabled(call.Tool))
            {
                var reason = ToolRegistry.GetDisableReason(call.Tool) ?? "未知原因";
                return new ToolResult { Status = "failed", Error = $"工具已禁用: {reason}" };
            }

            // 执行期权限组硬校验：即便工具因历史上下文被模型误调用，组外也一律拒绝
            if (PermissionGroup != null && !PermissionService.IsAllowed(PermissionGroup, call.Tool))
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"工具「{call.Tool}」不在当前权限组（{PermissionGroup}）内，不可用。请改用权限组内允许的工具。"
                };
            }

            using var cts = new CancellationTokenSource(tool.Timeout);
            try
            {
                var result = await tool.ExecuteAsync(call.Inputs, cts.Token);
                return result;
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
