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
        /// 每个工具执行完毕后立即触发的回调。用于即时处理副作用（如发送说话消息）。
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

            // 预授权检查：只查权限表，不阻塞
            if (tool.RequiredPermission > PermissionLevel.Default
                && authorizedTools != null
                && !authorizedTools.Contains(tool.Name))
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"未授权使用「{tool.Name}」，管理员可用 /auth grant {tool.Name} 授权"
                };
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