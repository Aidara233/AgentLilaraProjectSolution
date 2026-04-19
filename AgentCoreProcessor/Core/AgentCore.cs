using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 统一 Agent 核心。合并 ExpressCore + WorkingCore 的模型调用能力。
    /// 只负责模型调用和输出解析，不管循环、不管状态、不管副作用。
    /// </summary>
    internal class AgentCore : CoreBase
    {
        private string currentMode = "WorkingCore";

        public AgentCore()
        {
            processor.CfgName = "WorkingCore";
        }

        /// <summary>切换模式配置（Express/Working 用不同 LLM 配置）。</summary>
        public void SwitchMode(Engine.EngineMode mode)
        {
            var target = mode == Engine.EngineMode.Express ? "ExpressCore" : "WorkingCore";
            if (target == currentMode) return;
            currentMode = target;
            processor.CfgName = target;
        }

        /// <summary>
        /// 统一入口。根据模式调用模型，返回文本或工具调用。
        /// </summary>
        public async Task<ModelOutput> InvokeAsync(List<Message> messages, Engine.EngineMode mode)
        {
            SwitchMode(mode);
            ResetProcessor();
            SetConversationHistory(messages);

            if (mode == Engine.EngineMode.Express)
            {
                var text = await GenerateOnceAsync();
                return ModelOutput.FromText(text);
            }
            else
            {
                var calls = await GenerateToolCallsAsync();
                return ModelOutput.FromTools(calls);
            }
        }

        /// <summary>
        /// 单次生成（Express 模式）。
        /// </summary>
        public async Task<string> ChatAsync(string input, List<string>? imagePaths = null)
        {
            return imagePaths?.Count > 0
                ? await GenerateOnceAsync(input, imagePaths)
                : await GenerateOnceAsync(input);
        }

        /// <summary>
        /// 工具调用解析（Working 模式）。解析模型输出中的 JSON 工具调用块。
        /// </summary>
        public async Task<List<ToolCall>> GenerateToolCallsAsync()
        {
            var calls = new List<ToolCall>();
            await GenerateAsync(onBreak: block =>
            {
                var json = block.Content.Trim();
                if (string.IsNullOrEmpty(json)) return;
                try
                {
                    var call = ToolCall.FromJson(json);
                    if (!call.Validate().Any())
                        calls.Add(call);
                }
                catch { }
            });
            return calls;
        }

        /// <summary>
        /// 设置对话历史（供 WorkerEngine 在每轮准备阶段调用）。
        /// </summary>
        public void SetConversationHistory(List<Message> messages)
        {
            processor.Client.ClearConversationHistory();
            processor.Client.SetConversationHistory(messages);
        }
    }
}
