using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        public string? LastResult { get; private set; }

        private readonly ISystemContext ctx;
        private readonly AgentCore agentCore = new();
        private readonly Channel<string> instructionQueue = Channel.CreateUnbounded<string>();
        private readonly List<Message> conversationHistory = new();
        private readonly HashSet<string>? toolWhitelist;
        private readonly string? delegationId;
        private CancellationTokenSource? stopCts;
        private Task? backgroundTask;

        private const int MaxRoundsPerInstruction = 15;

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
            backgroundTask = Task.Run(() => RunLoopAsync(stopCts.Token));
            FrameworkLogger.Log("TaskSession", $"启动: {SessionId}, 指令: {initialInstruction.Truncate(80)}");
        }

        /// <summary>追加指令到队列（子 agent 空闲时处理）。</summary>
        public Task<bool> SendInstructionAsync(string instruction)
        {
            if (!IsAlive) return Task.FromResult(false);
            instructionQueue.Writer.TryWrite(instruction);
            FrameworkLogger.Log("TaskSession", $"追加指令: {SessionId}");
            return Task.FromResult(true);
        }

        public Task<bool> UpdateWatchRulesAsync(List<WatchRule> rules)
            => Task.FromResult(false);

        public void RequestStop()
        {
            FrameworkLogger.Log("TaskSession", $"停止请求: {SessionId}");
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
                FrameworkLogger.LogError("TaskSession", ex, $"子 agent 异常: {SessionId}");
                LastResult = $"异常终止: {ex.Message}";
            }
            finally
            {
                IsAlive = false;
                NotifyCompletion();
            }
        }

        private async Task ProcessInstructionAsync(string instruction, CancellationToken ct)
        {
            // 将指令加入对话历史
            conversationHistory.Add(new Message { Role = "user", Content = $"[指令] {instruction}" });

            List<ToolCall>? lastCalls = null;
            List<ToolResult>? lastResults = null;

            for (int round = 0; round < MaxRoundsPerInstruction && !ct.IsCancellationRequested; round++)
            {
                var messages = BuildMessages(lastCalls, lastResults);
                var output = await agentCore.InvokeAsync(messages, EngineMode.Working);

                if (output.IsText || output.ToolCalls == null || output.ToolCalls.Count == 0)
                {
                    // 模型返回文本 = 任务完成
                    LastResult = output.Thinking ?? output.Text ?? "(完成，无输出)";
                    conversationHistory.Add(new Message { Role = "assistant", Content = LastResult });
                    FrameworkLogger.Log("TaskSession",
                        $"指令完成: {SessionId}, rounds={round + 1}, result={LastResult.Truncate(100)}");
                    return;
                }

                // 执行工具
                var executor = new ToolExecutor(authorizedTools: toolWhitelist);
                var results = await executor.ExecuteAsync(output.ToolCalls);

                lastCalls = output.ToolCalls;
                lastResults = results;

                // 记录到对话历史
                var callSummary = string.Join("\n", output.ToolCalls.Select(c => $"{c.Tool}(...)"));
                conversationHistory.Add(new Message { Role = "assistant", Content = callSummary });
            }

            LastResult = "达到最大轮次限制";
            FrameworkLogger.Log("TaskSession", $"指令达到轮次上限: {SessionId}");
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
            // 如果关联了委托，更新委托状态
            if (!string.IsNullOrEmpty(delegationId))
            {
                var result = LastResult ?? "(无结果)";
                if (result.StartsWith("异常终止") || result == "达到最大轮次限制")
                    ctx.Delegations.MarkFailed(delegationId, result);
                else
                    ctx.Delegations.MarkCompleted(delegationId, result);
            }

            ctx.TaskBridge.PostNotification(new Notification
            {
                Type = NotificationType.ProgressUpdate,
                SourceId = SessionId,
                Summary = $"子 agent 完成: {LastResult?.Truncate(100) ?? "(无结果)"}",
                Timestamp = DateTime.Now
            });
        }
    }
}
