using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgentCoreProcessor.Engine;
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
            List<(string Description, bool Done)>? taskList = null,
            Dictionary<string, string>? pinboard = null,
            List<(string Summary, string FullContent)>? retainList = null,
            string? loopStatus = null)
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

            // 5.5 便签板（全量内容，Express/Working 共享）
            if (pinboard != null && pinboard.Count > 0)
            {
                var sb = new StringBuilder("[便签板]\n");
                foreach (var (label, content) in pinboard)
                    sb.AppendLine($"- {label}: {content}");
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            // 5.6 缓存列表（只显示序号+摘要，Working 专属）
            if (retainList != null && retainList.Count > 0)
            {
                var sb = new StringBuilder("[缓存列表]（使用「缓存管理」工具的 view 操作查看完整内容）\n");
                for (int i = 0; i < retainList.Count; i++)
                    sb.AppendLine($"{i + 1}. {retainList[i].Summary}");
                messages.Add(new Message { Role = "user", Content = sb.ToString() });
            }

            // 5.7 循环状态
            if (!string.IsNullOrEmpty(loopStatus))
                messages.Add(new Message { Role = "user", Content = loopStatus });

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

        /// <summary>
        /// 模块驱动的 prompt 组装。Phase 2+ 使用此方法。
        /// </summary>
        public List<Message> BuildRoundMessages(
            string toolDescriptions,
            string contextXml,
            List<EngineModule> modules,
            EngineMode mode,
            List<ToolResult>? lastRoundResults,
            List<ToolCall>? lastRoundCalls,
            List<string>? imagePaths = null,
            List<string>? newMessages = null)
        {
            var messages = new List<Message>();

            // 1. 工具描述
            messages.Add(new Message { Role = "user", Content = toolDescriptions });

            // 2. 对话上下文 XML
            var contextMsg = new Message { Role = "user", Content = contextXml };
            if (imagePaths != null && imagePaths.Count > 0)
            {
                var parts = new List<ContentPart>
                {
                    new() { Type = "text", Text = contextXml }
                };
                foreach (var path in imagePaths)
                    parts.Add(new ContentPart { Type = "image", ImagePath = path });
                contextMsg.ContentParts = parts;
            }
            messages.Add(contextMsg);

            // 3. 模块注入（按 PromptPriority 排序）
            foreach (var module in modules.OrderBy(m => m.PromptPriority))
            {
                var section = module.BuildPromptSection(mode);
                if (!string.IsNullOrEmpty(section))
                    messages.Add(new Message { Role = "user", Content = section });
            }

            // 4. 上一轮工具执行结果
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

            // 5. 运行时新消息
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
            {
                sb.Append($"[{call.Tool}]: {result.Status}");
                if (result.Error != null) sb.Append($" - {result.Error}");
                if (!string.IsNullOrWhiteSpace(result.Data))
                    sb.Append($"\n{result.Data}");
                sb.AppendLine();
            }
        }
    }
}
