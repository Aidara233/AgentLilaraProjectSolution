using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Client;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Tool;
using AgentCoreProcessor.Tool.Host;
using AgentLilara.PluginSDK;

namespace AgentCoreProcessor.Core
{
    /// <summary>
    /// 统一 Agent 核心。按配置名加载 Storage/Core/{name}.json（ExpressCore / ChannelCore / TaskCore），
    /// 由 RuntimeSession 多核切换按名懒建缓存。只负责模型调用和输出解析，不管循环、不管状态、不管副作用。
    /// </summary>
    internal class AgentCore : CoreBase
    {
        /// <summary>锁定的配置名：始终按该核加载（Express/Working 模式内切换已废弃，由会话多核切换取代）。</summary>
        private readonly string _configName = "Base";

        /// <summary>当前是否使用原生工具调用。</summary>
        internal bool UseNativeTools => processor?.Client?.Config?.UseNativeTools == true;

        /// <summary>当前模型是否禁用图片消息（DeepSeek 等不支持图片的 API）。</summary>
        internal bool DisableImageMessages => processor?.Client?.Config?.DisableImageMessages == true;

        /// <summary>需要模型"看"图片的工具——当 DisableImageMessages 时过滤掉。</summary>
        private static readonly HashSet<string> ImageDependentTools = new(StringComparer.OrdinalIgnoreCase)
        {
            "read_image", "refine_image"
        };

        /// <summary>额外的工具列表（loop 组件工具），合并到每次 API 请求的 tool_use 定义中。</summary>
        public List<ITool>? AdditionalTools { get; set; }

        /// <summary>全局组件工具列表，对所有循环可见。</summary>
        public List<ITool>? GlobalComponentTools { get; set; }

        /// <summary>
        /// 工具排除名集合（宿主用一个集合在 NonComponentToolNames → toolDefs 路径上额外过滤，
        /// 弥补仅有 AdditionalTools 过滤不充分的缺陷，例如 escalate 在 channel EngineType
        /// applicable，但不应出现在 Working session 内部工具表）。
        /// </summary>
        public IReadOnlySet<string>? ExcludedToolNames { get; set; }

        /// <summary>当前会话类型（express/channel/task/review），工具白/黑名单裁决依据。</summary>
        public string? EngineType { get; set; }

        /// <summary>
        /// session 本体提示词（由 RuntimeSession 从会话类型配置的 PromptFiles 构建）。
        /// 非空时覆盖 core 配置的 BasePrompt —— 换核只换模型配置，不换会话身份。
        /// </summary>
        public string? SessionPrompt { get; set; }

        public AgentCore(string cfgName) : base(cfgName)
        {
            _configName = cfgName;
        }

        /// <summary>
        /// 影子比较用的只读视图：客户端里**此刻**的会话历史（含 prepend 的系统提示与预设消息）。
        /// ⚠️ 不要拿它做生产逻辑——它绕过了 `InvokeAsync` 的装配次序。
        /// </summary>
        internal IModelClient ClientForBrain => processor.Client;

        /// <summary>实际作为系统提示发出去的那段文本（与 <see cref="SetConversationHistory"/> 同一条判据）。</summary>
        internal string EffectiveSystemPrompt =>
            !string.IsNullOrEmpty(SessionPrompt) ? SessionPrompt! : (processor.BasePrompt ?? "");

        /// <summary>
        /// 统一入口。会话统一走 Working 路径（Express 旧分支已随 express 会话化移除），
        /// 工具过滤由当前会话类型（EngineType）白/黑名单统一裁决。
        /// </summary>
        public async Task<ModelOutput> InvokeAsync(List<Message> messages, Engine.EngineMode mode)
        {
            // 一次性核配置：processor 缺失或已加载配置与目标核不符则用该核重建。EngineMode 为宿主侧遗留参数，不再分流。
            if (processor == null || processor.CfgName != _configName)
                processor = new Processor(_configName);
            ApplyExtraMessages();
            SetConversationHistory(messages);

            var (calls, thinking, usage) = await GenerateToolCallsWithThinkingAsync();
            return ModelOutput.FromTools(calls, thinking, usage);
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
                    catch (Exception ex)
                    {
                        Logging.Signal.Warn(Logging.LogGroup.Engine, $"解析工具调用失败: {json}", ex);
                    }
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
            // 只取非组件工具（核心 + MCP + 纯插件），按会话类型白/黑名单过滤
            var toolDefs = ToolRegistry.NonComponentToolNames
                .Select(ToolRegistry.Get)
                .Where(t => t != null
                            && ToolRegistry.IsVisible(t.Name, EngineType)
                            && (ExcludedToolNames == null || !ExcludedToolNames.Contains(t.Name))
                            && !(DisableImageMessages && ImageDependentTools.Contains(t.Name)))
                .Select(ToDefinition!)
                .ToList();

            // 合并全局组件工具（可见性由会话类型白/黑名单裁决）
            MergeTools(toolDefs, GlobalComponentTools);

            // 合并 loop 组件工具（已由 ComponentHost 按类型清单过滤）
            MergeTools(toolDefs, AdditionalTools);

            // 图片工具过滤（组件工具路径）
            if (DisableImageMessages)
                toolDefs.RemoveAll(td => ImageDependentTools.Contains(td.Name));

            NativeToolCallHandler handler = new(toolDefs);
            var usage = await GenerateWithToolsAsync(toolDefs, handler.OnEvent);
            var (calls, thinking) = handler.GetResult();
            return (calls, thinking, usage);
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
        /// 设置对话历史（供 ExpressEngine 在每轮准备阶段调用）。
        /// 自动 prepend 基础提示词：session 场景优先 SessionPrompt（会话本体，换核不变），
        /// 否则用 Processor 从 core 配置加载的 BasePrompt。
        /// </summary>
        public void SetConversationHistory(List<Message> messages)
        {
            var final = new List<Message>();
            var prompt = !string.IsNullOrEmpty(SessionPrompt) ? SessionPrompt : processor.BasePrompt;
            if (!string.IsNullOrEmpty(prompt))
                final.Add(new Message { Role = "system", Content = prompt });
            final.AddRange(messages);
            processor.Client.ClearConversationHistory();
            processor.Client.SetConversationHistory(final);
        }

        /// <summary>
        /// 工具对当前会话类型的可见性判定。统一走 ToolRegistry.IsVisible
        /// （全局禁用 + 会话类型白/黑名单裁决）。组件工具也接受同一裁决。
        /// </summary>
        private bool IsVisibleToEngineOrGlobal(ITool t)
        {
            return ToolRegistry.IsVisible(t.Name, EngineType);
        }

        private void MergeTools(List<ToolDefinition> defs, List<ITool>? tools)
        {
            if (tools == null) return;
            foreach (var t in tools)
            {
                if (!IsVisibleToEngineOrGlobal(t)) continue;
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
