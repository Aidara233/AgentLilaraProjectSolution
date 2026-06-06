using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK;

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

        /// <summary>当前是否使用原生工具调用。</summary>
        internal bool UseNativeTools => processor?.Client?.Config?.UseNativeTools == true;

        /// <summary>额外的工具列表（loop 组件工具），合并到每次 API 请求的 tool_use 定义中。</summary>
        public List<ITool>? AdditionalTools { get; set; }

        /// <summary>全局组件工具列表，对所有循环可见。</summary>
        public List<ITool>? GlobalComponentTools { get; set; }

        /// <summary>当前引擎类型（如 "channel"、"system"、"review"、"sub-agent"），用于工具过滤。</summary>
        public string? EngineType { get; set; }

        /// <summary>当前模式 ID（如 "express"、"plan"、"build"）。null 时跳过模式过滤（子 agent 等场景）。</summary>
        public string? CurrentModeId { get; set; }

        public AgentCore() : base("WorkingCore")
        {
        }

        public AgentCore(string cfgName) : base(cfgName)
        {
            currentMode = cfgName;
            fixedMode = true;
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
        /// 统一入口。根据模式和 profile 调用模型，返回文本或工具调用。
        /// </summary>
        public async Task<ModelOutput> InvokeAsync(List<Message> messages, Engine.EngineMode mode)
        {
            SwitchMode(mode);
            if (processor == null || processor.CfgName != currentMode)
                processor = new Processor(currentMode);
            ApplyExtraMessages();
            SetConversationHistory(messages);

            if (mode == Engine.EngineMode.Express)
            {
                var expressDefs = ToolRegistry.GetExpressToolDefinitions(EngineType);
                // 合并全局组件工具
                if (GlobalComponentTools != null)
                {
                    foreach (var t in GlobalComponentTools)
                    {
                        if (ToolRegistry.IsDisabled(t.Name)) continue;
                        if (!ModeConfigLoader.IsToolEnabled("express", t.Name)) continue;
                        if (expressDefs.Any(d => d.Name == t.Name)) continue;
                        expressDefs.Add(ToDefinition(t));
                    }
                }
                // 合并 loop 组件工具
                if (AdditionalTools != null)
                {
                    foreach (var t in AdditionalTools)
                    {
                        if (ToolRegistry.IsDisabled(t.Name)) continue;
                        if (!ModeConfigLoader.IsToolEnabled("express", t.Name)) continue;
                        if (expressDefs.Any(d => d.Name == t.Name)) continue;
                        expressDefs.Add(ToDefinition(t));
                    }
                }
                if (expressDefs.Count > 0 && UseNativeTools)
                {
                    var (text, calls, thinking, usage) = await GenerateExpressWithToolsAsync(expressDefs);
                    return ModelOutput.FromExpressWithTools(text, calls.Count > 0 ? calls : null, thinking, usage);
                }
                else
                {
                    var text = await GenerateOnceAsync();
                    return ModelOutput.FromText(text);
                }
            }
            else
            {
                var (calls, thinking, usage) = await GenerateToolCallsWithThinkingAsync();
                return ModelOutput.FromTools(calls, thinking, usage);
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
        /// 工具调用解析（Working 模式）。UseNativeTools 时使用原生 tool_use，否则解析文本 JSON。
        /// </summary>
        public async Task<(List<ToolCall> Calls, string? Thinking, Usage? Usage)> GenerateToolCallsWithThinkingAsync()
        {
            if (UseNativeTools)
                return await GenerateWithNativeToolsAsync();

            // 旧路径：文本 JSON 解析
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
                    if (jsonStart > 0)
                    {
                        var thinking = raw[..jsonStart].Trim();
                        if (thinking.Length > 0)
                            thinkingParts.Add(thinking);
                    }

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
                    if (raw.Length > 0)
                        thinkingParts.Add(raw);
                }
            });
            var thinking = thinkingParts.Count > 0 ? string.Join("\n", thinkingParts) : null;
            return (calls, thinking, null);
        }

        private async Task<(List<ToolCall> Calls, string? Thinking, Usage? Usage)> GenerateWithNativeToolsAsync()
        {
            // 只取非组件工具（核心 + MCP + 纯插件），按引擎类型和模式过滤
            var toolDefs = ToolRegistry.NonComponentToolNames
                .Select(ToolRegistry.Get)
                .Where(t => t != null && !ToolRegistry.IsDisabled(t.Name)
                            && ToolRegistry.IsApplicableToEngine(t.Name, EngineType)
                            && (CurrentModeId == null || ModeConfigLoader.IsToolEnabled(CurrentModeId, t.Name)))
                .Select(ToDefinition!)
                .ToList();

            // 合并全局组件工具（已由引擎按 EngineType 过滤）
            MergeTools(toolDefs, GlobalComponentTools);

            // 合并 loop 组件工具（已由 ComponentHost 过滤）
            MergeTools(toolDefs, AdditionalTools);

            NativeToolCallHandler handler = new(toolDefs);
            var usage = await GenerateWithToolsAsync(toolDefs, handler.OnEvent);
            var (calls, thinking) = handler.GetResult();
            return (calls, thinking, usage);
        }

        private async Task<(string Text, List<ToolCall> Calls, string? Thinking, Usage? Usage)> GenerateExpressWithToolsAsync(
            List<ToolDefinition> expressDefs)
        {
            ExpressToolCallHandler handler = new(expressDefs);
            var usage = await GenerateWithToolsAsync(expressDefs, handler.OnEvent);
            var (text, calls, thinking) = handler.GetResult();
            return (text, calls, thinking, usage);
        }

        /// <summary>
        /// 工具调用解析（Working 模式）。兼容旧接口。
        /// </summary>
        public async Task<List<ToolCall>> GenerateToolCallsAsync()
        {
            var (calls, _, _) = await GenerateToolCallsWithThinkingAsync();
            return calls;
        }

        /// <summary>
        /// 系统循环专用：复用 Processor 实例，直接设置历史并调用。
        /// </summary>
        public async Task<ModelOutput> InvokeWithHistoryAsync(List<Message> messages)
        {
            processor ??= new Processor(currentMode);
            processor.Client.ClearConversationHistory();
            processor.Client.SetConversationHistory(messages);
            var (calls, thinking, usage) = await GenerateToolCallsWithThinkingAsync();
            return ModelOutput.FromTools(calls, thinking, usage);
        }

        /// <summary>
        /// 设置对话历史（供 ChannelEngine 在每轮准备阶段调用）。
        /// 自动 prepend Processor 加载的基础提示词。
        /// </summary>
        public void SetConversationHistory(List<Message> messages)
        {
            var final = new List<Message>();
            if (!string.IsNullOrEmpty(processor.BasePrompt))
                final.Add(new Message { Role = "system", Content = processor.BasePrompt });
            final.AddRange(messages);
            processor.Client.ClearConversationHistory();
            processor.Client.SetConversationHistory(final);
        }

        private void MergeTools(List<ToolDefinition> defs, List<ITool>? tools)
        {
            if (tools == null) return;
            foreach (var t in tools)
            {
                if (ToolRegistry.IsDisabled(t.Name)) continue;
                if (CurrentModeId != null && !ModeConfigLoader.IsToolEnabled(CurrentModeId, t.Name)) continue;
                if (defs.Any(d => d.Name == t.Name)) continue;
                defs.Add(ToDefinition(t));
            }
        }

        private static ToolDefinition ToDefinition(ITool t) => new()
        {
            Name = t.Name,
            Description = t.Description,
            Parameters = t.GetInputSchema()
        };
    }
}
