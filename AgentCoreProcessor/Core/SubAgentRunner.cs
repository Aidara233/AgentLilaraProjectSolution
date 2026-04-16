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
    /// 参照 ReviewEngine 的循环模式，但更简化：无人设、无消息感知、不可嵌套委派。
    /// </summary>
    internal static class SubAgentRunner
    {
        private const int MaxRounds = 8;
        private const string CompletionToolName = "完成";

        /// <summary>
        /// 执行子 agent 任务。
        /// </summary>
        /// <param name="taskDescription">自然语言任务描述</param>
        /// <param name="tools">可用工具集（已过滤 AllowSubAgent=false）</param>
        /// <param name="record">执行记录，每轮追加日志</param>
        /// <returns>完成摘要或错误信息</returns>
        public static async Task<string> RunAsync(
            string taskDescription, IEnumerable<ITool> tools, SubAgentRecord record)
        {
            var toolDict = tools.ToDictionary(t => t.Name);
            Func<string, ITool?> resolver = name =>
                toolDict.TryGetValue(name, out var t) ? t : null;

            var core = new SubAgentCore();
            var promptBuilder = new PromptBuilder();
            var register = new Dictionary<string, string>();
            var retainedResults = new List<(ToolCall call, ToolResult result)>();
            List<ToolCall>? lastCalls = null;
            List<ToolResult>? lastResults = null;

            var toolDescriptions = ToolRegistry.GenerateDescriptions(toolDict.Values);

            for (int round = 0; round < MaxRounds; round++)
            {
                // 组装消息
                var messages = promptBuilder.BuildRoundMessages(
                    toolDescriptions,
                    taskDescription,
                    new Dictionary<string, string>(), // 子 agent 不用思考笔记
                    lastResults, lastCalls, retainedResults);

                core.SetConversation(messages);

                // 解析工具调用
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

                // 执行
                var executor = new ToolExecutor(register, resolver);
                var results = await executor.ExecuteAsync(toolCalls);

                // 记录日志
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    var call = toolCalls[i];
                    var result = results[i];
                    var status = result.IsSuccess ? "成功" : $"失败({result.Error})";
                    record.Log.Add($"[轮{round + 1}] {call.Tool}: {status}");
                }

                // 检查完成工具
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    if (toolCalls[i].Tool == CompletionToolName && results[i].IsSuccess)
                    {
                        var summary = results[i].Data ?? "任务完成";
                        record.Status = "completed";
                        record.Summary = summary;
                        record.Log.Add($"[完成] {summary}");
                        return summary;
                    }
                }

                // 收集 retain 结果
                for (int i = 0; i < toolCalls.Count; i++)
                {
                    if (toolCalls[i].Retain && results[i].IsSuccess)
                        retainedResults.Add((toolCalls[i], results[i]));
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
