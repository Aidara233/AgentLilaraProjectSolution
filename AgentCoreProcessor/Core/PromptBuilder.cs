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
        /// useNativeTools=true 时跳过文本工具描述，工具结果以 tool_result content block 回传。
        /// </summary>
        public List<Message> BuildRoundMessages(
            string toolDescriptions,
            string contextXml,
            List<EngineModule> modules,
            EngineMode mode,
            List<ToolResult>? lastRoundResults,
            List<ToolCall>? lastRoundCalls,
            List<ImageEmbed>? imageEmbeds = null,
            bool useNativeTools = false)
        {
            var messages = new List<Message>();

            // 1. 工具描述（native 模式跳过）
            if (!useNativeTools)
                messages.Add(new Message { Role = "user", Content = toolDescriptions });

            // 2. 对话上下文 XML + 图片
            var contextMsg = new Message { Role = "user", Content = contextXml };
            if (imageEmbeds != null && imageEmbeds.Count > 0)
            {
                var parts = new List<ContentPart> { ContentPart.FromText(contextXml) };
                foreach (var embed in imageEmbeds)
                {
                    parts.Add(ContentPart.FromText($"#IMG{embed.ImageId}:"));
                    parts.Add(ContentPart.FromImagePath(embed.Path));
                }
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
                if (useNativeTools)
                {
                    BuildNativeToolResults(messages, lastRoundCalls, lastRoundResults);
                }
                else
                {
                    BuildTextToolResults(messages, lastRoundCalls, lastRoundResults);
                }
            }

            return messages;
        }

        private static void BuildNativeToolResults(
            List<Message> messages, List<ToolCall> calls, List<ToolResult> results)
        {
            // 构建 assistant 消息（含 tool_use 块）
            var toolUseParts = new List<ContentPart>();
            for (int i = 0; i < calls.Count; i++)
            {
                if (calls[i].ToolUseId != null)
                {
                    // input 必须是 JSON object，按工具参数定义重建
                    var inputJson = RebuildInputJson(calls[i]);
                    toolUseParts.Add(ContentPart.FromToolUse(
                        calls[i].ToolUseId!, calls[i].Tool, inputJson));
                }
            }
            if (toolUseParts.Count > 0)
            {
                var asstMsg = new Message
                {
                    Role = "assistant",
                    Content = "[tool calls]",
                    ContentParts = toolUseParts
                };
                messages.Add(asstMsg);
            }

            // 构建 user 消息（含 tool_result 块）
            var resultParts = new List<ContentPart>();
            for (int i = 0; i < calls.Count && i < results.Count; i++)
            {
                if (calls[i].ToolUseId != null)
                {
                    var data = results[i].IsSuccess
                        ? (results[i].Data ?? "成功")
                        : $"失败: {results[i].Error ?? results[i].Status}";
                    resultParts.Add(ContentPart.FromToolResult(
                        calls[i].ToolUseId!, data, !results[i].IsSuccess));
                }
            }
            if (resultParts.Count > 0)
            {
                var resultMsg = new Message
                {
                    Role = "user",
                    Content = "[tool results]",
                    ContentParts = resultParts
                };
                messages.Add(resultMsg);
            }
        }

        private static void BuildTextToolResults(
            List<Message> messages, List<ToolCall> calls, List<ToolResult> results)
        {
            var sb = new StringBuilder("上一轮工具执行结果：\n");
            var resultAttachments = new List<ContentPart>();
            for (int i = 0; i < calls.Count; i++)
            {
                if (i < results.Count)
                {
                    FormatSingleResult(sb, calls[i], results[i]);
                    if (results[i].Attachments != null)
                        resultAttachments.AddRange(results[i].Attachments!.Select(a =>
                            a.Base64Data != null
                                ? ContentPart.FromImageBase64(a.Base64Data, a.MediaType ?? "image/png")
                                : ContentPart.FromImagePath(a.FilePath ?? "")));
                }
            }

            var resultMsg = new Message { Role = "user", Content = sb.ToString() };
            if (resultAttachments.Count > 0)
            {
                var parts = new List<ContentPart> { ContentPart.FromText(sb.ToString()) };
                parts.AddRange(resultAttachments);
                resultMsg.ContentParts = parts;
            }
            messages.Add(resultMsg);
        }

        private static void FormatSingleResult(StringBuilder sb, ToolCall call, ToolResult result)
        {
            var tool = ToolRegistry.Get(call.Tool);
            if (tool?.GetContinueLoop() == true && result.IsSuccess)
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

        /// <summary>
        /// 将 positional inputs 重建为 JSON object（Claude API 要求 tool_use.input 必须是 object）。
        /// </summary>
        private static string RebuildInputJson(ToolCall call)
        {
            if (call.Inputs.Count == 0) return "{}";

            var tool = ToolRegistry.Get(call.Tool);
            if (tool == null)
            {
                // 工具不存在时用 arg0/arg1 作为 key
                var fallback = new Dictionary<string, string>();
                for (int i = 0; i < call.Inputs.Count; i++)
                    fallback[$"arg{i}"] = call.Inputs[i];
                return Newtonsoft.Json.JsonConvert.SerializeObject(fallback);
            }

            var obj = new Dictionary<string, string>();
            var parameters = tool.Parameters;
            for (int i = 0; i < call.Inputs.Count; i++)
            {
                var key = i < parameters.Count ? parameters[i].Name : $"arg{i}";
                obj[key] = call.Inputs[i];
            }
            return Newtonsoft.Json.JsonConvert.SerializeObject(obj);
        }
    }
}
