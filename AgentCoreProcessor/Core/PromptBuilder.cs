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
        /// <summary>
        /// 模块驱动的 prompt 组装。Express/Working 统一使用此方法。
        /// </summary>
        public List<Message> BuildRoundMessages(
            string toolDescriptions,
            string contextXml,
            List<EngineModule> modules,
            EngineMode mode,
            List<ToolResult>? lastRoundResults,
            List<ToolCall>? lastRoundCalls,
            List<string>? imagePaths = null)
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
