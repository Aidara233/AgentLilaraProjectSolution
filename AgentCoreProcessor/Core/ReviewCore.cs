using System.Collections.Generic;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 复盘核心。继承 CoreBase 获取 LLM 通信能力（GenerateAsync + break 解析）。
    /// 由 ReviewEngine 在 Agent 循环中使用。
    /// </summary>
    internal class ReviewCore : CoreBase
    {
        public bool UseNativeTools => processor?.Client?.Config?.UseNativeTools == true;

        /// <summary>清除并设置对话历史，供 ReviewEngine 外部调用。</summary>
        public void SetConversation(List<Message> messages)
        {
            processor.Client.ClearConversationHistory();
            processor.Client.SetConversationHistory(messages);
        }

        /// <summary>原生工具调用路径。返回解析后的 ToolCall 列表、思考文本和 Usage。</summary>
        public async System.Threading.Tasks.Task<(List<ToolCall> Calls, string? Thinking, Usage Usage)>
            GenerateToolCallsWithToolsAsync(List<ToolDefinition> toolDefs)
        {
            var handler = new NativeToolCallHandler(toolDefs);
            var usage = await GenerateWithToolsAsync(toolDefs, handler.OnEvent);
            var (calls, thinking) = handler.GetResult();
            return (calls, thinking, usage);
        }
    }
}
