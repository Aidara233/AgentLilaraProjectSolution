using System.Collections.Generic;
using System.Text;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 提示词组装器。为 Agent 循环的每一轮构建动态消息列表。
    /// 生成的消息作为 ConversationHistory 注入 IModelClient，
    /// 与 PresetMessages（系统提示）合并后发送给模型。
    /// </summary>
    internal class PromptBuilder
    {
        /// <summary>
        /// 构建 Agent 循环单轮的动态消息列表。
        /// </summary>
        /// <param name="toolDescriptions">ToolRegistry.GenerateDescriptions() 生成的工具描述文本</param>
        /// <param name="userRequest">用户原始需求，每轮固定不变</param>
        /// <param name="thinkingNotes">模型维护的思考笔记 key-value</param>
        /// <param name="lastRoundResults">上一轮工具执行结果（滚动，仅最近一轮）</param>
        /// <param name="lastRoundCalls">上一轮的工具调用（与 lastRoundResults 一一对应）</param>
        /// <param name="retainedResults">被 retain=true 标记的历史结果（跨轮保留）</param>
        /// <param name="additionalContext">附加上下文（预留给记忆系统注入）</param>
        /// <param name="imagePaths">图片路径列表（仅首轮注入）</param>
        /// <param name="newMessages">运行时到达的新消息（WorkerEngine 推送）</param>
        /// <param name="subAgentResults">已完成的子 agent 结果</param>
        /// <returns>组装好的消息列表，设置为 ConversationHistory 即可</returns>
        public List<Message> BuildRoundMessages(
            string toolDescriptions,
            string userRequest,
            Dictionary<string, string> thinkingNotes,
            List<ToolResult>? lastRoundResults,
            List<ToolCall>? lastRoundCalls,
            List<(ToolCall call, ToolResult result)> retainedResults,
            string? additionalContext = null,
            List<string>? imagePaths = null,
            List<string>? newMessages = null,
            List<(string id, string summary)>? subAgentResults = null)
        {
            var messages = new List<Message>();

            // 1. 工具描述（每轮固定，但内容由 ToolRegistry 动态生成）
            messages.Add(new Message { Role = "user", Content = toolDescriptions });

            // 2. 附加上下文（预留记忆注入点，当前可选）
            if (!string.IsNullOrEmpty(additionalContext))
                messages.Add(new Message { Role = "user", Content = $"补充上下文：\n{additionalContext}" });

            // 3. 用户原始需求（有图片时附加多模态内容块）
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

            // 4. 思考笔记（模型通过思考笔记工具维护，每轮全量注入）
            if (thinkingNotes.Count > 0)
            {
                var sb = new StringBuilder("你的思考笔记：\n");
                foreach (var (key, value) in thinkingNotes)
                    sb.AppendLine($"- {key}: {value}");
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            // 5. 历史保留结果（retain=true 的工具结果，跨轮持久）
            if (retainedResults.Count > 0)
            {
                var sb = new StringBuilder("历史轮次保留的工具结果：\n");
                foreach (var (call, result) in retainedResults)
                    FormatSingleResult(sb, call, result);
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            // 6. 上一轮的工具执行结果（滚动窗口，只保留最近一轮）
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

            // 7. 运行时新消息（WorkerEngine 推送的新到达消息）
            if (newMessages != null && newMessages.Count > 0)
            {
                var sb = new StringBuilder("[新消息到达]\n");
                foreach (var msg in newMessages)
                    sb.AppendLine(msg);
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            // 8. 子 agent 完成结果
            if (subAgentResults != null && subAgentResults.Count > 0)
            {
                var sb = new StringBuilder("[子任务完成]\n");
                foreach (var (id, summary) in subAgentResults)
                    sb.AppendLine($"{id}: {summary}");
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            return messages;
        }

        /// <summary>
        /// 格式化单条工具结果。根据 OutputToModel 决定是否包含完整返回值。
        /// </summary>
        private static void FormatSingleResult(StringBuilder sb, ToolCall call, ToolResult result)
        {
            if (call.OutputToModel && result.IsSuccess)
                sb.AppendLine($"[{call.ToolId}] {call.Tool}: 成功，返回值：{result.Data}");
            else if (result.IsSuccess)
                sb.AppendLine($"[{call.ToolId}] {call.Tool}: 成功");
            else
                sb.AppendLine($"[{call.ToolId}] {call.Tool}: {result.Status}" +
                    (result.Error != null ? $" - {result.Error}" : ""));
        }
    }
}
