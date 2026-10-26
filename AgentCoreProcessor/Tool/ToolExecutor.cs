using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Logging;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Tool
{
    /// <summary>
    /// 工具执行器。顺序执行工具调用列表，支持禁用检查和权限检查。
    /// </summary>
    internal class ToolExecutor
    {
        private readonly Func<string, ITool?> toolResolver;

        /// <summary>当前会话类型（express/channel/task/review）。设置后执行期硬校验：类型外工具返回 failed。</summary>
        public string? PermissionGroup { get; set; }

        /// <summary>发起本次执行的会话 id（进入 <see cref="ToolExecutionScope"/>，供需要的工具读取）。</summary>
        public string? SessionId { get; set; }

        /// <summary>该会话当前占用的频道；0 表示没有频道。</summary>
        public int ChannelId { get; set; }

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
            // 每个工具调用一条独立 span（open 带完整输入，close 带状态/数据/错误），
            // 让 trace 页展示单个工具的完整流程与耗时，而不是整轮工具的笼统阶段。
            using var span = Signal.Open(LogGroup.Tool, $"工具: {call.Tool}",
                new { tool = call.Tool, toolUseId = call.ToolUseId, inputs = call.Inputs });

            // 工具拿不到会话信息（ITool 签名里没有），需要的那几个从这里读。
            // 用 using 成对恢复：异步并发下漏掉清理会把上下文串到别的会话。
            using var scope = ToolExecutionScope.Begin(new ToolExecutionContext
            {
                SessionId = SessionId,
                SessionType = PermissionGroup,
                ChannelId = ChannelId,
            });

            var result = await ExecuteToolCoreAsync(call);
            span.SetCloseDetail(new { status = result.Status, data = result.Data, error = result.Error });
            return result;
        }

        private async Task<ToolResult> ExecuteToolCoreAsync(ToolCall call)
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

            // 执行期权限组硬校验：即便工具因历史上下文被模型误调用，类型外也一律拒绝
            if (PermissionGroup != null && !Engine.Session.SessionTypeProfiles.IsAllowed(PermissionGroup, call.Tool))
            {
                return new ToolResult
                {
                    Status = "failed",
                    Error = $"工具「{call.Tool}」不在当前会话类型（{PermissionGroup}）允许范围内，不可用。请改用允许的工具。"
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
