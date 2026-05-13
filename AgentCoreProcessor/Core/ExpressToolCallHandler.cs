using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// Express 模式流事件处理器。分离文本回复和 fire-and-forget 工具调用。
    /// 与 NativeToolCallHandler 的区别：Text 事件是模型的回复内容，不是 thinking。
    /// </summary>
    internal class ExpressToolCallHandler
    {
        private readonly List<ToolDefinition> toolDefs;

        private readonly List<ToolCall> calls = new();
        private readonly StringBuilder textParts = new();
        private readonly List<string> thinkingParts = new();

        private string? currentToolUseId;
        private string? currentToolName;
        private readonly StringBuilder currentArgsJson = new();

        public ExpressToolCallHandler(List<ToolDefinition> toolDefs)
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

                case StreamEventType.Text:
                    if (evt.Content != null)
                        textParts.Append(evt.Content);
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
            }
        }

        private void FinalizeCurrentCall()
        {
            if (currentToolName == null) return;

            var call = new ToolCall { Tool = currentToolName, ToolUseId = currentToolUseId };
            var argsStr = currentArgsJson.ToString();

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
                            foreach (var (_, val) in args)
                                call.Inputs.Add(val?.ToString() ?? "");
                        }
                    }
                }
                catch
                {
                    call.Inputs.Add(argsStr);
                }
            }

            calls.Add(call);
            currentToolUseId = null;
            currentToolName = null;
            currentArgsJson.Clear();
        }

        public (string Text, List<ToolCall> Calls, string? Thinking) GetResult()
        {
            var text = textParts.ToString();
            var thinking = thinkingParts.Count > 0 ? string.Join("", thinkingParts) : null;
            return (text, calls, thinking);
        }
    }
}
