using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 从原生工具调用流事件中累积并解析 ToolCall 列表。
    /// 处理 ToolUseStart → ToolUseDelta → ToolUseEnd 生命周期。
    /// </summary>
    internal class NativeToolCallHandler
    {
        private readonly List<ToolDefinition> toolDefs;

        private readonly List<ToolCall> calls = new();
        private readonly List<string> thinkingParts = new();

        private string? currentToolUseId;
        private string? currentToolName;
        private readonly StringBuilder currentArgsJson = new();

        public NativeToolCallHandler(List<ToolDefinition> toolDefs)
        {
            this.toolDefs = toolDefs;
        }

        public void OnEvent(StreamEvent evt)
        {
            switch (evt.Type)
            {
                case StreamEventType.Thinking:
                    if (evt.Content != null)
                        thinkingParts.Add(evt.Content);
                    break;

                case StreamEventType.ToolUseStart:
                    currentToolUseId = evt.ToolUseId;
                    currentToolName = evt.ToolName;
                    currentArgsJson.Clear();
                    break;

                case StreamEventType.ToolUseDelta:
                    if (evt.Content != null)
                        currentArgsJson.Append(evt.Content);
                    break;

                case StreamEventType.ToolUseEnd:
                    FinalizeCurrentCall();
                    break;

                case StreamEventType.Text:
                    // 文本内容忽略（工具模式下模型不应该输出自由文本）
                    if (evt.Content != null)
                        thinkingParts.Add(evt.Content);
                    break;
            }
        }

        private void FinalizeCurrentCall()
        {
            if (currentToolName == null) return;

            var call = new ToolCall { Tool = currentToolName, ToolUseId = currentToolUseId };
            var argsStr = currentArgsJson.ToString();
            call.RawInputJson = string.IsNullOrWhiteSpace(argsStr) ? "{}" : argsStr;

            // 将命名的 JSON 参数映射到 positional inputs
            if (!string.IsNullOrWhiteSpace(argsStr))
            {
                try
                {
                    var args = JsonNode.Parse(argsStr) as JsonObject;
                    if (args != null)
                    {
                        var def = toolDefs.Find(t => t.Name == currentToolName);
                        if (def?.Parameters is JsonObject schema
                            && schema["properties"] is JsonObject properties)
                        {
                            // 按 properties 定义顺序映射（与 ITool.Parameters 顺序一致）
                            foreach (var (key, _) in properties)
                            {
                                if (args.TryGetPropertyValue(key, out var val))
                                    call.Inputs.Add(val?.ToString() ?? "");
                                else
                                    call.Inputs.Add("");
                            }
                        }
                        else
                        {
                            // 无 schema 信息时，按属性顺序填入
                            foreach (var (_, val) in args)
                                call.Inputs.Add(val?.ToString() ?? "");
                        }
                    }
                }
                catch
                {
                    // 解析失败时填入原始 JSON
                    call.Inputs.Add(argsStr);
                }
            }

            calls.Add(call);
            currentToolUseId = null;
            currentToolName = null;
            currentArgsJson.Clear();
        }

        public (List<ToolCall> Calls, string? Thinking) GetResult()
        {
            var thinking = thinkingParts.Count > 0 ? string.Join("", thinkingParts) : null;
            return (calls, thinking);
        }
    }
}
