using System.Collections.Generic;
using System.IO;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Engine.Modules;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 原生工具消息格式化。把模型输出的 tool_use 与执行结果的 tool_result 配对为 API 历史消息。
    /// Agent 与 RuntimeSession 共用，确保 assistant(tool_use) 与 user(tool_result) 严格成对、携带 tool_use_id。
    /// </summary>
    internal static class NativeToolMessageFormatter
    {
        /// <summary>
        /// 把 ModelOutput 格式化为 assistant 历史消息。
        /// 原生工具模式下保留 tool_use ContentParts（含 tool_use_id），否则降级为文本。
        /// </summary>
        public static Message FormatAssistant(ModelOutput output, bool useNativeTools, System.Func<string, ITool?> toolResolver)
        {
            if (output.HasToolCalls && output.ToolCalls != null)
            {
                if (useNativeTools)
                {
                    var parts = new List<ContentPart>();
                    if (!string.IsNullOrEmpty(output.Thinking))
                        parts.Add(ContentPart.FromText(output.Thinking));
                    foreach (var c in output.ToolCalls)
                    {
                        if (c.ToolUseId != null)
                        {
                            var json = BuildToolInputJson(c, toolResolver);
                            parts.Add(ContentPart.FromToolUse(c.ToolUseId, c.Tool, json));
                        }
                    }
                    return new Message
                    {
                        Role = "assistant",
                        Content = !string.IsNullOrEmpty(output.Thinking) ? output.Thinking : "[tool calls]",
                        ContentParts = parts
                    };
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    if (!string.IsNullOrEmpty(output.Thinking))
                        sb.AppendLine(output.Thinking);
                    foreach (var c in output.ToolCalls)
                        sb.AppendLine($"{c.Tool}({string.Join(", ", c.Inputs).Truncate(100)})");
                    return new Message { Role = "assistant", Content = sb.ToString() };
                }
            }
            var content = output.Text ?? output.Thinking;
            if (string.IsNullOrEmpty(content))
                content = "[无输出]";
            return new Message { Role = "assistant", Content = content };
        }

        /// <summary>
        /// 把工具调用与结果配对为 user 历史消息。
        /// 原生模式输出 tool_result ContentParts（含 tool_use_id + 图片附件），否则降级为文本汇总。
        /// </summary>
        public static Message FormatToolResults(List<ToolCall> calls, List<ToolResult> results, bool useNativeTools)
        {
            if (useNativeTools)
            {
                var parts = new List<ContentPart>();
                for (int i = 0; i < calls.Count && i < results.Count; i++)
                {
                    if (calls[i].ToolUseId != null)
                    {
                        var data = results[i].IsSuccess
                            ? (results[i].Data ?? "成功")
                            : $"失败: {results[i].Error ?? results[i].Status}";
                        parts.Add(ContentPart.FromToolResult(calls[i].ToolUseId!, data, !results[i].IsSuccess));

                        // 图片附件：在 tool_result 后追加 image ContentPart
                        var attachments = results[i].Attachments;
                        if (results[i].IsSuccess && attachments != null)
                        {
                            foreach (var att in attachments)
                            {
                                if (att.Type == "image")
                                {
                                    if (!string.IsNullOrEmpty(att.FilePath) && File.Exists(att.FilePath))
                                        parts.Add(ContentPart.FromImagePath(att.FilePath));
                                    else if (!string.IsNullOrEmpty(att.Base64Data))
                                        parts.Add(ContentPart.FromImageBase64(att.Base64Data, att.MediaType ?? "image/png"));
                                }
                            }
                        }
                    }
                }
                return new Message { Role = "user", Content = "[tool results]", ContentParts = parts };
            }
            else
            {
                var sb = new System.Text.StringBuilder("[上一轮工具执行结果]\n");
                for (int i = 0; i < calls.Count && i < results.Count; i++)
                {
                    if (results[i].IsSuccess)
                        sb.AppendLine($"[{calls[i].Tool}]: 成功");
                    else
                        sb.AppendLine($"[{calls[i].Tool}]: {results[i].Status} - {results[i].Error}");
                }
                return new Message { Role = "user", Content = sb.ToString() };
            }
        }

        /// <summary>
        /// 确保 tool_use 的 input 始终是 JSON 对象（非数组）。各 API 均要求 tool_use.input 为 JSON object，数组格式会导致 400。
        /// </summary>
        public static string BuildToolInputJson(ToolCall call, System.Func<string, ITool?> toolResolver)
        {
            // 优先用 RawInputJson，但验证它是对象
            if (!string.IsNullOrEmpty(call.RawInputJson))
            {
                try
                {
                    var node = Newtonsoft.Json.Linq.JToken.Parse(call.RawInputJson);
                    if (node is Newtonsoft.Json.Linq.JObject)
                        return call.RawInputJson;
                }
                catch { }
            }

            // 从工具 schema 重建具名 JSON 对象
            var tool = toolResolver(call.Tool);
            if (tool != null)
            {
                try
                {
                    var schemaNode = tool.GetInputSchema();
                    var schemaStr = schemaNode?.ToJsonString();
                    if (!string.IsNullOrEmpty(schemaStr))
                    {
                        var schema = Newtonsoft.Json.Linq.JObject.Parse(schemaStr);
                        if (schema["properties"] is Newtonsoft.Json.Linq.JObject props)
                        {
                            var obj = new Newtonsoft.Json.Linq.JObject();
                            int i = 0;
                            foreach (var prop in props.Properties())
                            {
                                var raw = i < call.Inputs.Count ? call.Inputs[i] : "";
                                Newtonsoft.Json.Linq.JToken val;
                                try { val = Newtonsoft.Json.Linq.JToken.Parse(raw); }
                                catch { val = raw; }
                                obj[prop.Name] = val;
                                i++;
                            }
                            return obj.ToString(Newtonsoft.Json.Formatting.None);
                        }
                    }
                }
                catch { }
            }

            // 最后兜底：空对象
            return "{}";
        }
    }
}
