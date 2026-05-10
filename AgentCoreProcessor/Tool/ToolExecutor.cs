using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Modules;

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
            {
                FrameworkLogger.Log("ToolExecutor", $"未知工具: {call.Tool}");
                return new ToolResult { Status = "failed", Error = $"未知工具: {call.Tool}" };
            }

            // 禁用检查
            if (ToolRegistry.IsDisabled(call.Tool))
            {
                var reason = ToolRegistry.GetDisableReason(call.Tool) ?? "未知原因";
                FrameworkLogger.Log("ToolExecutor", $"工具已禁用: {call.Tool} ({reason})");
                return new ToolResult { Status = "failed", Error = $"工具已禁用: {reason}" };
            }

            // 预授权检查：只查权限表，不阻塞
            if (tool.RequiredPermission > PermissionLevel.Default
                && authorizedTools != null
                && !authorizedTools.Contains(tool.Name))
            {
                FrameworkLogger.Log("ToolExecutor", $"未授权: {call.Tool}");
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"未授权使用「{tool.Name}」，管理员可用 /auth grant {tool.Name} 授权"
                };
            }

            var inputSummary = call.Inputs.Count > 0
                ? string.Join(", ", call.Inputs).Truncate(120)
                : "(无参数)";
            FrameworkLogger.Log("ToolExecutor", $"执行: {call.Tool}({inputSummary})");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(tool.Timeout);
            try
            {
                var result = await tool.ExecuteAsync(call.Inputs, cts.Token);
                sw.Stop();

                var dataSummary = result.Data != null ? result.Data.Truncate(200) : "";
                if (result.IsSuccess)
                    FrameworkLogger.Log("ToolExecutor",
                        $"完成: {call.Tool} → {result.Status}, {sw.ElapsedMilliseconds}ms" +
                        (dataSummary.Length > 0 ? $", data={dataSummary}" : ""));
                else
                    FrameworkLogger.Log("ToolExecutor",
                        $"失败: {call.Tool} → {result.Status}: {result.Error}, {sw.ElapsedMilliseconds}ms" +
                        (dataSummary.Length > 0 ? $"\n  data={dataSummary}" : ""));

                return result;
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                FrameworkLogger.Log("ToolExecutor",
                    $"超时: {call.Tool}, {sw.ElapsedMilliseconds}ms (limit={tool.Timeout.TotalSeconds}s)");
                return new ToolResult { Status = "failed", Error = $"执行超时（{tool.Timeout.TotalSeconds}s）" };
            }
            catch (Exception ex)
            {
                sw.Stop();
                FrameworkLogger.Log("ToolExecutor",
                    $"异常: {call.Tool}, {sw.ElapsedMilliseconds}ms, {ex.GetType().Name}: {ex.Message}");
                return new ToolResult { Status = "failed", Error = ex.Message };
            }
        }
    }
}