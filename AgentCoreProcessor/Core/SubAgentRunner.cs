using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 子 agent 执行器。接收自然语言任务描述和工具集，跑独立的 Agent 循环返回结果。
    /// 当前已禁用（DelegateTool 未注册），保留代码供未来启用。
    /// </summary>
    internal static class SubAgentRunner
    {
        private const int MaxRounds = 8;

        public static async Task<string> RunAsync(
            string taskDescription, IEnumerable<ITool> tools, SubAgentRecord record)
        {
            var toolDict = tools.ToDictionary(t => t.Name);
            Func<string, ITool?> resolver = name =>
                toolDict.TryGetValue(name, out var t) ? t : null;

            var core = new SubAgentCore();
            var promptBuilder = new PromptBuilder();
            List<ToolCall>? lastCalls = null;
            List<ToolResult>? lastResults = null;

            var toolDescriptions = ToolRegistry.GenerateDescriptions(toolDict.Values);

            for (int round = 0; round < MaxRounds; round++)
            {
                var messages = promptBuilder.BuildRoundMessages(
                    toolDescriptions,
                    taskDescription,
                    new Dictionary<string, string>(),
                    lastResults, lastCalls);

                core.SetConversation(messages);

                var toolCalls = new List<ToolCall>();
                await core.GenerateAsync(onBreak: (block) =>
                {
                    var json = block.Content.Trim();
                    if (string.IsNullOrEmpty(json)) return;
                    try
                    {
                        var call = ToolCall.FromJson(json);
                        if (!call.Validate().Any())
                            toolCalls.Add(call);
                    }
                    catch { }
                });

                if (toolCalls.Count == 0)
                {
                    record.Log.Add($"[轮{round + 1}] 无工具调用，隐式完成");
                    break;
                }

                var executor = new ToolExecutor(resolver);
                var results = await executor.ExecuteAsync(toolCalls);

                for (int i = 0; i < toolCalls.Count; i++)
                {
                    var call = toolCalls[i];
                    var result = results[i];
                    var status = result.IsSuccess ? "成功" : $"失败({result.Error})";
                    record.Log.Add($"[轮{round + 1}] {call.Tool}: {status}");
                }

                // 子 agent 没有 ContinueLoop 概念，用 0 工具调用隐式完成
                // 检查是否所有工具都是非 ContinueLoop（自然结束）
                bool hasContinue = toolCalls.Any(c =>
                {
                    var tool = resolver(c.Tool);
                    return tool?.ContinueLoop == true;
                });

                if (!hasContinue)
                {
                    var summary = results.LastOrDefault(r => r.IsSuccess)?.Data ?? "任务完成";
                    record.Status = "completed";
                    record.Summary = summary;
                    record.Log.Add($"[完成] {summary}");
                    return summary;
                }

                lastCalls = toolCalls;
                lastResults = results;
            }

            record.Status = "failed";
            record.Summary = "达到轮次上限，未能完成";
            record.Log.Add("[失败] 达到最大轮次限制");
            return "[子任务] 达到轮次上限，未能完成";
        }
    }
}
