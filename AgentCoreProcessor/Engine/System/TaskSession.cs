using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 任务会话（一次性子 agent）。
    /// 接收指令，调用模型，执行工具，返回结果。用完销毁。
    /// </summary>
    internal class TaskSession : IAgentSession
    {
        public string SessionId { get; }
        public AgentSessionType Type => AgentSessionType.Task;
        public bool IsAlive { get; private set; } = true;

        private readonly ISystemContext ctx;
        private readonly AgentCore agentCore = new();

        public TaskSession(ISystemContext ctx)
        {
            this.ctx = ctx;
            this.SessionId = $"task-{Guid.NewGuid()}";
        }

        /// <summary>
        /// 发送指令给任务会话。
        /// 构建 prompt（系统状态 + 指令），调用模型，执行工具，返回结果文本。
        /// </summary>
        public async Task<bool> SendInstructionAsync(string instruction)
        {
            if (!IsAlive) return false;

            try
            {
                // ① 构建 prompt
                var messages = BuildPromptMessages(instruction);

                // ② 调用模型（Working 模式）
                var output = await agentCore.InvokeAsync(messages, EngineMode.Working);

                // ③ 执行工具调用
                if (output.ToolCalls != null && output.ToolCalls.Count > 0)
                {
                    var executor = new ToolExecutor();
                    var results = await executor.ExecuteAsync(output.ToolCalls);

                    // ④ 返回结果文本
                    var resultText = string.Join("\n", results.Select(r =>
                    {
                        if (r.IsSuccess)
                            return $"[{r.Status}] {r.Data ?? "(无数据)"}";
                        else
                            return $"[{r.Status}] {r.Error ?? "(未知错误)"}";
                    }));

                    FrameworkLogger.Log("TaskSession",
                        $"任务完成: sessionId={SessionId}, 工具数={output.ToolCalls.Count}");

                    return true;
                }

                // 如果模型返回文本（不应该发生，但容错处理）
                if (output.IsText)
                {
                    var textPreview = output.Text != null && output.Text.Length > 100
                        ? output.Text.Substring(0, 100)
                        : output.Text ?? "";
                    FrameworkLogger.Log("TaskSession",
                        $"任务返回文本: sessionId={SessionId}, text={textPreview}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                FrameworkLogger.LogError("TaskSession", ex, $"任务执行失败: sessionId={SessionId}");
                return false;
            }
        }

        /// <summary>
        /// 任务会话不支持更新关注规则。
        /// </summary>
        public Task<bool> UpdateWatchRulesAsync(List<WatchRule> rules)
        {
            return Task.FromResult(false);
        }

        /// <summary>
        /// 请求停止会话。
        /// </summary>
        public void RequestStop()
        {
            IsAlive = false;
        }

        /// <summary>
        /// 构建 prompt 消息列表。
        /// 包含系统状态摘要 + 当前指令。
        /// </summary>
        private List<Message> BuildPromptMessages(string instruction)
        {
            var messages = new List<Message>();

            // 系统状态摘要
            var statusSummary = BuildSystemStatusSummary();
            if (!string.IsNullOrEmpty(statusSummary))
            {
                messages.Add(new Message
                {
                    Role = "user",
                    Content = statusSummary
                });
            }

            // 工具描述（全量工具）
            var toolDescs = ToolRegistry.GenerateDescriptions();
            messages.Add(new Message
            {
                Role = "user",
                Content = $"[可用工具]\n{toolDescs}"
            });

            // 当前指令
            messages.Add(new Message
            {
                Role = "user",
                Content = $"[任务指令]\n{instruction}"
            });

            return messages;
        }

        /// <summary>
        /// 构建系统状态摘要。
        /// 包含引擎状态、空闲时长等关键信息。
        /// </summary>
        private string BuildSystemStatusSummary()
        {
            var summary = "[系统状态]\n";
            summary += $"- 空闲状态: {(ctx.IsIdle ? "是" : "否")}\n";
            summary += $"- 空闲时长: {ctx.IdleDuration.TotalMinutes:F1} 分钟\n";
            summary += $"- 最后消息时间: {ctx.LastMessageTime:yyyy-MM-dd HH:mm:ss}\n";

            var engineSummary = ctx.GetActiveEngineSummary();
            if (engineSummary.Count > 0)
            {
                summary += "- 活跃引擎:\n";
                foreach (var (type, count) in engineSummary)
                {
                    summary += $"  - {type}: {count} 个实例\n";
                }
            }

            return summary;
        }
    }
}

