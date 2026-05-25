using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Models;
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
        private readonly bool noPersona;

        /// <summary>当前是否使用原生工具调用。</summary>
        internal bool UseNativeTools => processor?.Client?.Config?.UseNativeTools == true;

        /// <summary>工具 Profile 管理器（由引擎注入）。</summary>
        public ToolProfileManager? ProfileManager { get; set; }

        /// <summary>额外的工具列表（loop 组件工具），合并到每次 API 请求的 tool_use 定义中。</summary>
        public List<ITool>? AdditionalTools { get; set; }

        /// <summary>全局组件工具列表，对所有循环可见。</summary>
        public List<ITool>? GlobalComponentTools { get; set; }

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
        /// 统一入口。根据模式和 profile 调用模型，返回文本或工具调用。
        /// </summary>
        public async Task<ModelOutput> InvokeAsync(List<Message> messages, Engine.EngineMode mode, string? profileName = null)
        {
            SwitchMode(mode);
            processor = new Processor(currentMode, usePersona: UsePersona);
            ApplyExtraMessages();
            SetConversationHistory(messages);

            if (mode == Engine.EngineMode.Express)
            {
                var expressDefs = ToolRegistry.GetExpressToolDefinitions();
                // 合并全局组件中 ExpressAvailable 的工具
                if (GlobalComponentTools != null)
                {
                    foreach (var t in GlobalComponentTools)
                    {
                        if (ToolRegistry.IsDisabled(t.Name)) continue;
                        if (!IsExpressAvailable(t)) continue;
                        if (expressDefs.Any(d => d.Name == t.Name)) continue;
                        expressDefs.Add(ToDefinition(t));
                    }
                }
                // 合并 loop 组件中 ExpressAvailable 的工具
                if (AdditionalTools != null)
                {
                    foreach (var t in AdditionalTools)
                    {
                        if (ToolRegistry.IsDisabled(t.Name)) continue;
                        if (!IsExpressAvailable(t)) continue;
                        if (expressDefs.Any(d => d.Name == t.Name)) continue;
                        expressDefs.Add(ToDefinition(t));
                    }
                }
                if (expressDefs.Count > 0 && UseNativeTools)
                {
                    var (text, calls, thinking) = await GenerateExpressWithToolsAsync(expressDefs);
                    return ModelOutput.FromExpressWithTools(text, calls.Count > 0 ? calls : null, thinking);
                }
                else
                {
                    var text = await GenerateOnceAsync();
                    return ModelOutput.FromText(text);
                }
            }
            else
            {
                var (calls, thinking) = await GenerateToolCallsWithThinkingAsync(profileName);
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
        /// 工具调用解析（Working 模式）。UseNativeTools 时使用原生 tool_use，否则解析文本 JSON。
        /// </summary>
        public async Task<(List<ToolCall> Calls, string? Thinking)> GenerateToolCallsWithThinkingAsync(string? profileName = null)
        {
            if (UseNativeTools)
                return await GenerateWithNativeToolsAsync(profileName);

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
            return (calls, thinking);
        }

        private async Task<(List<ToolCall> Calls, string? Thinking)> GenerateWithNativeToolsAsync(string? profileName)
        {
            // 从 ToolProfileManager 获取工具定义，不再依赖 ToolFilter
            List<ToolDefinition> toolDefs;
            if (ProfileManager != null && profileName != null)
            {
                toolDefs = ProfileManager.GetToolDefinitions(profileName);
            }
            else
            {
                // fallback：使用全部已注册工具（Core + MCP，不含组件工具）
                toolDefs = ToolRegistry.All.Values
                    .Where(t => !ToolRegistry.IsDisabled(t.Name))
                    .Select(ToDefinition)
                    .ToList();
            }

            // 合并全局组件工具
            MergeTools(toolDefs, GlobalComponentTools);

            // 合并 loop 组件工具
            MergeTools(toolDefs, AdditionalTools);

            NativeToolCallHandler handler = new(toolDefs);
            await GenerateWithToolsAsync(toolDefs, handler.OnEvent);
            return handler.GetResult();
        }

        private async Task<(string Text, List<ToolCall> Calls, string? Thinking)> GenerateExpressWithToolsAsync(
            List<ToolDefinition> expressDefs)
        {
            ExpressToolCallHandler handler = new(expressDefs);
            await GenerateWithToolsAsync(expressDefs, handler.OnEvent);
            return handler.GetResult();
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
        /// </summary>
        public async Task<ModelOutput> InvokeWithHistoryAsync(List<Message> messages, string? profileName = null)
        {
            processor ??= new Processor(currentMode, usePersona: UsePersona);
            processor.Client.ClearConversationHistory();
            processor.Client.SetConversationHistory(messages);
            var (calls, thinking) = await GenerateToolCallsWithThinkingAsync(profileName);
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

        private static void MergeTools(List<ToolDefinition> defs, List<ITool>? tools)
        {
            if (tools == null) return;
            foreach (var t in tools)
            {
                if (ToolRegistry.IsDisabled(t.Name)) continue;
                if (defs.Any(d => d.Name == t.Name)) continue;
                defs.Add(ToDefinition(t));
            }
        }

        private static bool IsExpressAvailable(ITool t)
        {
            var meta = ToolRegistry.GetMeta(t.Name)
                ?? Attribute.GetCustomAttribute(t.GetType(), typeof(ToolMetaAttribute)) as ToolMetaAttribute;
            return meta?.ExpressAvailable == true;
        }

        private static ToolDefinition ToDefinition(ITool t) => new()
        {
            Name = t.Name,
            Description = t.Description,
            Parameters = t.GetInputSchema()
        };
    }
}
