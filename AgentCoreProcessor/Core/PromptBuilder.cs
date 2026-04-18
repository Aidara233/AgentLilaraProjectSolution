using System.Collections.Generic;
using System.Text;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 提示词组装器。为 Agent 循环的每一轮构建动态消息列表。
    /// </summary>
    internal class PromptBuilder
    {
        public List<Message> BuildRoundMessages(
            string toolDescriptions,
            string userRequest,
            Dictionary<string, string> thinkingNotes,
            List<ToolResult>? lastRoundResults,
            List<ToolCall>? lastRoundCalls,
            string? additionalContext = null,
            List<string>? imagePaths = null,
            List<string>? newMessages = null,
            List<(string Description, bool Done)>? taskList = null)
        {
            var messages = new List<Message>();

            // 1. 工具描述
            messages.Add(new Message { Role = "user", Content = toolDescriptions });

            // 2. 附加上下文
            if (!string.IsNullOrEmpty(additionalContext))
                messages.Add(new Message { Role = "user", Content = $"补充上下文：\n{additionalContext}" });

            // 3. 用户原始需求
            var requestMsg = new Message { Role = "user", Content = $"用户需求：{userRequest}" };
            if (imagePaths != null && imagePaths.Count > 0)
            {
                var parts = new List<ContentPart>
                {
                    new() { Type = "text", Text = $"用户需求：{userRequest}" }
                };
                foreach (var path in imagePaths)
                    parts.Add(new ContentPart { Type = "image", ImagePath = path });
                requestMsg.ContentParts = parts;
            }
            messages.Add(requestMsg);

            // 4. 思考笔记
            if (thinkingNotes.Count > 0)
            {
                var sb = new StringBuilder("你的思考笔记：\n");
                foreach (var (key, value) in thinkingNotes)
                    sb.AppendLine($"- {key}: {value}");
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            // 5. 任务列表
            if (taskList != null && taskList.Count > 0)
            {
                var sb = new StringBuilder("[当前任务]\n");
                for (int i = 0; i < taskList.Count; i++)
                {
                    var (desc, done) = taskList[i];
                    var mark = done ? "\u2713" : " ";
                    sb.AppendLine($"{i + 1}. [{mark}] {desc}");
                }
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            // 6. 上一轮工具执行结果
            if (lastRoundResults != null && lastRoundCalls != null && lastRoundResults.Count > 0)
            {
                var sb = new StringBuilder("上一轮工具执行结果：\n");
                for (int i = 0; i < lastRoundCalls.Count; i++)
                {
                    if (i < lastRoundResults.Count)
                        FormatSingleResult(sb, lastRoundCalls[i], lastRoundResults[i]);
                }
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            // 7. 运行时新消息
            if (newMessages != null && newMessages.Count > 0)
            {
                var sb = new StringBuilder("[新消息到达]\n");
                foreach (var msg in newMessages)
                    sb.AppendLine(msg);
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            return messages;
        }

        private static void FormatSingleResult(StringBuilder sb, ToolCall call, ToolResult result)
        {
            var tool = ToolRegistry.Get(call.Tool);
            if (tool?.ContinueLoop == true && result.IsSuccess)
                sb.AppendLine($"[{call.Tool}]: 成功，返回值：{result.Data}");
            else if (result.IsSuccess)
                sb.AppendLine($"[{call.Tool}]: 成功");
            else
                sb.AppendLine($"[{call.Tool}]: {result.Status}" +
                    (result.Error != null ? $" - {result.Error}" : ""));
        }
    }
}
