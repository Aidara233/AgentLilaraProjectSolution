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
        private readonly bool fixedMode;
        private readonly bool noPersona;

        protected override bool UsePersona => !noPersona;

        public AgentCore() : base("WorkingCore")
        {
        }

        public AgentCore(string cfgName, bool usePersona = true) : base(cfgName)
        {
            currentMode = cfgName;
            fixedMode = true;
            noPersona = !usePersona;
            if (!usePersona)
                processor = new Processor(cfgName, usePersona: false);
        }

        /// <summary>切换模式配置（Express/Working 用不同 LLM 配置）。固定模式时不切换。</summary>
        public void SwitchMode(Engine.EngineMode mode)
        {
            if (fixedMode) return;
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
            processor = new Processor(currentMode, usePersona: UsePersona);
            ApplyExtraMessages();
            SetConversationHistory(messages);

            if (mode == Engine.EngineMode.Express)
            {
                var text = await GenerateOnceAsync();
                return ModelOutput.FromText(text);
            }
            else
            {
                var (calls, thinking) = await GenerateToolCallsWithThinkingAsync();
                return ModelOutput.FromTools(calls, thinking);
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
        /// 支持模型在 JSON 前输出思考文本（会被提取并保留）。
        /// </summary>
        public async Task<(List<ToolCall> Calls, string? Thinking)> GenerateToolCallsWithThinkingAsync()
        {
            var calls = new List<ToolCall>();
            var thinkingParts = new List<string>();
            await GenerateAsync(onBreak: block =>
            {
                var raw = block.Content.Trim();
                if (string.IsNullOrEmpty(raw)) return;

                var jsonStart = raw.IndexOf('{');
                var jsonEnd = raw.LastIndexOf('}');

                if (jsonStart >= 0 && jsonEnd > jsonStart)
                {
                    // 提取思考文本（JSON 之前的部分）
                    if (jsonStart > 0)
                    {
                        var thinking = raw[..jsonStart].Trim();
                        if (thinking.Length > 0)
                            thinkingParts.Add(thinking);
                    }

                    // 提取并解析 JSON
                    var json = raw[jsonStart..(jsonEnd + 1)];
                    try
                    {
                        var call = ToolCall.FromJson(json);
                        if (!call.Validate().Any())
                            calls.Add(call);
                    }
                    catch { }
                }
                else
                {
                    // 整个 block 都是文本（无 JSON）
                    if (raw.Length > 0)
                        thinkingParts.Add(raw);
                }
            });
            var thinking = thinkingParts.Count > 0 ? string.Join("\n", thinkingParts) : null;
            return (calls, thinking);
        }

        /// <summary>
        /// 工具调用解析（Working 模式）。兼容旧接口。
        /// </summary>
        public async Task<List<ToolCall>> GenerateToolCallsAsync()
        {
            var (calls, _) = await GenerateToolCallsWithThinkingAsync();
            return calls;
        }

        /// <summary>
        /// 系统循环专用：复用 Processor 实例，直接设置历史并调用。
        /// 避免每轮重建 Processor（重读配置、重注入 persona）。
        /// </summary>
        public async Task<ModelOutput> InvokeWithHistoryAsync(List<Message> messages)
        {
            processor ??= new Processor(currentMode, usePersona: UsePersona);
            processor.Client.ClearConversationHistory();
            processor.Client.SetConversationHistory(messages);
            var (calls, thinking) = await GenerateToolCallsWithThinkingAsync();
            return ModelOutput.FromTools(calls, thinking);
        }

        /// <summary>
        /// 设置对话历史（供 ChannelEngine 在每轮准备阶段调用）。
        /// </summary>
        public void SetConversationHistory(List<Message> messages)
        {
            processor.Client.ClearConversationHistory();
            processor.Client.SetConversationHistory(messages);
        }
    }
}
