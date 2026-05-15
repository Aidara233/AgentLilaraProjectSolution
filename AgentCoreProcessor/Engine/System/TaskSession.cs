using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 异步任务会话（子 agent）。
    /// 后台运行 Agent 循环，支持追加指令，完成后通知系统循环。
    /// </summary>
    internal class TaskSession : IAgentSession
    {
        public string SessionId { get; }
        public AgentSessionType Type => AgentSessionType.Task;
        public bool IsAlive { get; private set; } = true;
        public string? CurrentInstruction { get; private set; }
        public string? LastResult { get; internal set; }

        private readonly ISystemContext ctx;
        private readonly AgentCore agentCore = new();
        private readonly Channel<string> instructionQueue = Channel.CreateUnbounded<string>();
        private readonly List<Message> conversationHistory = new();
        private readonly HashSet<string>? toolWhitelist;
        private readonly string? delegationId;
        private CancellationTokenSource? stopCts;
        private Task? backgroundTask;

        private const int MaxRoundsPerInstruction = 15;
        private const int MaxInvokeRetries = 3;

        public TaskSession(ISystemContext ctx, string? delegationId = null, HashSet<string>? toolWhitelist = null)
        {
            this.ctx = ctx;
            this.delegationId = delegationId;
            this.toolWhitelist = toolWhitelist;
            this.SessionId = $"task-{Guid.NewGuid().ToString("N")[..8]}";
            agentCore.CallerTag = $"SubAgent:{SessionId}";
        }

        /// <summary>启动后台 Agent 循环。</summary>
        public void Start(string initialInstruction)
        {
            stopCts = new CancellationTokenSource();
            instructionQueue.Writer.TryWrite(initialInstruction);
            instructionQueue.Writer.Complete();
            backgroundTask = Task.Run(() => RunLoopAsync(stopCts.Token));
        }

        /// <summary>追加指令到队列（子 agent 空闲时处理）。</summary>
        public Task<bool> SendInstructionAsync(string instruction)
        {
            if (!IsAlive) return Task.FromResult(false);
            return Task.FromResult(false);
        }

        public Task<bool> UpdateWatchRulesAsync(List<WatchRule> rules)
            => Task.FromResult(false);

        public void RequestStop()
        {
            stopCts?.Cancel();
            instructionQueue.Writer.TryComplete();
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var instruction in instructionQueue.Reader.ReadAllAsync(ct))
                {
                    CurrentInstruction = instruction;
                    await ProcessInstructionAsync(instruction, ct);
                    CurrentInstruction = null;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LastResult = $"异常终止: {ex.Message}";
            }
            finally
            {
                IsAlive = false;
                NotifyCompletion();
            }
        }

        internal bool _taskDoneSignaled;

        private async Task ProcessInstructionAsync(string instruction, CancellationToken ct)
        {
            conversationHistory.Add(new Message { Role = "user", Content = $"[指令] {instruction}" });

            var taskDoneTool = new TaskDoneTool(this);
            ToolRegistry.Register(taskDoneTool);

            List<ToolCall>? lastCalls = null;
            List<ToolResult>? lastResults = null;

            try
            {
                for (int round = 0; round < MaxRoundsPerInstruction && !ct.IsCancellationRequested; round++)
                {
                    var messages = BuildMessages(lastCalls, lastResults);

                    // 单轮 API 调用：指数退避重试瞬时错误
                    var maybeOutput = await InvokeWithRetryAsync(messages, ct);
                    if (maybeOutput == null)
                    {
                        LastResult = "API 调用连续失败，子 agent 中止";
                        return;
                    }
                    var output = maybeOutput.Value;

                    if (output.IsText || output.ToolCalls == null || output.ToolCalls.Count == 0)
                    {
                        LastResult = output.Thinking ?? output.Text ?? "(完成，无输出)";
                        conversationHistory.Add(new Message { Role = "assistant", Content = LastResult });
                        return;
                    }

                    var executor = new ToolExecutor(authorizedTools: toolWhitelist);
                    var results = await executor.ExecuteAsync(output.ToolCalls);

                    if (_taskDoneSignaled)
                    {
                        return;
                    }

                    lastCalls = output.ToolCalls;
                    lastResults = results;

                    var callSummary = string.Join("\n", output.ToolCalls.Select(c => $"{c.Tool}(...)"));
                    conversationHistory.Add(new Message { Role = "assistant", Content = callSummary });
                }

                LastResult = "达到最大轮次限制";
            }
            finally
            {
                ToolRegistry.Unregister("task_done");
            }
        }

        private async Task<ModelOutput?> InvokeWithRetryAsync(List<Message> messages, CancellationToken ct)
        {
            for (int attempt = 0; attempt < MaxInvokeRetries; attempt++)
            {
                try
                {
                    return await agentCore.InvokeAsync(messages, EngineMode.Working);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    if (attempt < MaxInvokeRetries - 1)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                        await Task.Delay(delay, ct);
                    }
                }
            }
            return null;
        }

        private List<Message> BuildMessages(List<ToolCall>? lastCalls, List<ToolResult>? lastResults)
        {
            var messages = new List<Message>();

            // 系统状态
            messages.Add(new Message
            {
                Role = "user",
                Content = BuildSystemStatus()
            });

            // 工具描述（仅非原生模式需要文本描述，原生模式通过 API tools 参数传递）
            if (!agentCore.UseNativeTools)
            {
                var toolDescs = ToolRegistry.GenerateDescriptions(authorizedTools: toolWhitelist);
                if (!string.IsNullOrEmpty(toolDescs))
                    messages.Add(new Message { Role = "user", Content = toolDescs });
            }

            // 对话历史（最近 10 轮）
            var recentHistory = conversationHistory.TakeLast(20).ToList();
            if (recentHistory.Count > 0)
            {
                var historyText = string.Join("\n", recentHistory.Select(m => $"[{m.Role}] {m.Content}"));
                messages.Add(new Message { Role = "user", Content = $"[对话历史]\n{historyText}" });
            }

            // 上一轮工具结果
            if (lastCalls != null && lastResults != null)
            {
                var sb = new StringBuilder("[上一轮工具结果]\n");
                for (int i = 0; i < lastCalls.Count && i < lastResults.Count; i++)
                {
                    var call = lastCalls[i];
                    var result = lastResults[i];
                    sb.AppendLine(result.IsSuccess
                        ? $"[{call.Tool}]: {result.Data ?? "成功"}"
                        : $"[{call.Tool}]: 失败 - {result.Error}");
                }
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            return messages;
        }

        private string BuildSystemStatus()
        {
            var sb = new StringBuilder("[子 agent 状态]\n");
            sb.AppendLine($"会话 ID: {SessionId}");
            sb.AppendLine($"当前时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"系统空闲: {(ctx.IsIdle ? "是" : "否")}");
            sb.AppendLine("提示: 完成任务后直接输出结果文本（不调用工具），循环会自动结束。");
            return sb.ToString();
        }

        private void NotifyCompletion()
        {
            try
            {
                var result = LastResult ?? "(无结果)";
                var isFailed = result.StartsWith("异常终止") || result == "达到最大轮次限制"
                    || result == "API 调用连续失败，子 agent 中止";

                if (!string.IsNullOrEmpty(delegationId))
                {
                    if (isFailed)
                    {
                        // 失败：标记 RetryPending，双通知
                        ctx.Delegations.MarkRetryPending(delegationId, result);

                        // 通知系统循环：子 agent 失败，需决策
                        ctx.TaskBridge.PostNotification(new Notification
                        {
                            Type = NotificationType.SubAgentFailed,
                            SourceId = SessionId,
                            DelegationId = delegationId,
                            Summary = $"子 agent 执行失败: {result.Truncate(100)}",
                            Timestamp = DateTime.Now
                        });

                        // 通知频道循环：让用户知情
                        var delegation = ctx.Delegations.Get(delegationId);
                        if (delegation != null)
                        {
                            var channelMsg = $"[系统] 委托「{delegation.Description.Truncate(30)}」执行遇到问题: {result.Truncate(60)}。系统正在评估是否重试（已重试 {delegation.RetryCount}/{DelegationRegistry.MaxRetries} 次）。";
                            ctx.NotifyChannel(delegation.SourceChannelId, channelMsg);
                        }
                    }
                    else
                    {
                        ctx.Delegations.MarkCompleted(delegationId, result);
                    }
                }
                else
                {
                    // 无委托关联的子 agent，仅发进度通知
                    ctx.TaskBridge.PostNotification(new Notification
                    {
                        Type = isFailed ? NotificationType.SubAgentFailed : NotificationType.ProgressUpdate,
                        SourceId = SessionId,
                        Summary = $"子 agent {(isFailed ? "失败" : "完成")}: {result.Truncate(100)}",
                        Timestamp = DateTime.Now
                    });
                }
            }
            catch (Exception ex)
            {
            }
        }
    }

    /// <summary>
    /// 子 agent 专用工具：标记任务完成并汇报结果。
    /// </summary>
    internal class TaskDoneTool : ITool
    {
        private readonly TaskSession _session;

        public TaskDoneTool(TaskSession session) { _session = session; }

        public string Name => "task_done";
        public string Description => "任务完成时调用此工具汇报结果。调用后子agent将立即停止。";
        public IReadOnlyList<ToolParameter> Parameters =>
        [
            new("result", "任务执行结果摘要", 0)
        ];
        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
        {
            var result = resolvedInputs.Count > 0 ? resolvedInputs[0] : "(无结果)";
            _session.LastResult = result;
            _session._taskDoneSignaled = true;
            return Task.FromResult(new ToolResult { Status = "success", Data = "任务已标记完成。" });
        }

        public JsonNode GetInputSchema() => JsonNode.Parse("""
        {
            "type": "object",
            "properties": {
                "result": { "type": "string", "description": "任务执行结果摘要" }
            },
            "required": ["result"]
        }
        """)!;
    }
}
