using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using AgentCoreProcessor.Models;

namespace AgentCoreProcessor.Client
{
    [JsonObject(MemberSerialization.OptIn)]
    public class ApiClientCfg
    {
        // 基本配置
        [JsonProperty("apiKey")]
        public string ApiKey { get; set; } = "";

        [JsonProperty("apiEndpoint")]
        public string ApiEndpoint { get; set; } = "https://api.siliconflow.cn/v1/chat/completions";

        [JsonProperty("model")]
        public string Model { get; set; } = "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B";

        [JsonProperty("temperature")]
        public double Temperature { get; set; } = 0.7;

        [JsonProperty("maxTokens")]
        public int? MaxTokens { get; set; } = null;

        [JsonProperty("topP")]
        public double? TopP { get; set; } = null;

        [JsonProperty("frequencyPenalty")]
        public double? FrequencyPenalty { get; set; } = null;

        [JsonProperty("presencePenalty")]
        public double? PresencePenalty { get; set; } = null;

        [JsonProperty("stream")]
        public bool Stream { get; set; } = true;

        // 通用：向 API 请求体注入额外字段。各客户端自行决定读取方式：
        // - ClaudeModelClient: 读取 "thinking" → 映射到 SDK ThinkingParameters
        // - OpenAIModelClient: 全部透传到 HTTP 请求体
        [JsonProperty("extraBody")]
        public Dictionary<string, object>? ExtraBody { get; set; } = null;

        /// <summary>
        /// 向 ExtraBody 添加或更新一个键值。
        /// </summary>
        public void AddExtraBody(string key, object value)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key 不能为空", nameof(key));
            ExtraBody ??= new Dictionary<string, object>();
            ExtraBody[key] = value;
        }

        [JsonProperty("n")]
        public int N { get; set; } = 1;

        // 模型协议提供者：openai（默认）或 claude
        [JsonProperty("provider")]
        public string Provider { get; set; } = "openai";

        // Claude 专用：API 版本号。provider 不为 claude 时忽略。
        [JsonProperty("anthropicVersion")]
        public string? AnthropicVersion { get; set; } = null;

        // 通用：启用请求级缓存。各客户端自行实现：
        // - Claude: 在 system/前缀消息上设置 cache_control
        // - OpenAI: 无客户端控制，服务端自动缓存，usage 报告 cached tokens
        [JsonProperty("enableCaching")]
        public bool EnableCaching { get; set; } = false;

        // 向后兼容：旧配置中的 "promptCaching" 反序列化到此
        [JsonProperty("promptCaching")]
        public bool PromptCaching { get; set; } = false;

        /// <summary>任一为 true 即启用缓存。</summary>
        public bool ShouldEnableCaching() => EnableCaching || PromptCaching;

        // 启用原生工具调用（Anthropic tool_use / OpenAI function calling）
        [JsonProperty("useNativeTools")]
        public bool UseNativeTools { get; set; } = false;

        // 强制模型调用工具（tool_choice: required/any），默认 false=auto
        [JsonProperty("forceToolCall")]
        public bool ForceToolCall { get; set; } = false;

        // 禁用 image_url 消息格式（DeepSeek 等不支持图片的兼容 API 需设为 true）
        [JsonProperty("disableImageMessages")]
        public bool DisableImageMessages { get; set; } = false;

        // 预设消息模板（从配置文件加载，system prompt + few-shot 示例）
        [JsonProperty("conversationHistory")]
        public List<Message> PresetMessages { get; set; } = new List<Message>();

        // 无参构造函数（用于手动创建或序列化库）
        public ApiClientCfg() { }

        // 将当前对象序列化为 JSON 字符串
        public string ToJson(bool indented = false)
        {
            return JsonConvert.SerializeObject(this, indented ? Formatting.Indented : Formatting.None);
        }

        // 从 JSON 字符串反序列化为 ApiClientCfg 对象
        public static ApiClientCfg FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new ApiClientCfg();
            }

            try
            {
                var cfg = JsonConvert.DeserializeObject<ApiClientCfg>(json);
                return cfg ?? new ApiClientCfg();
            }
            catch (JsonException)
            {
                // 返回默认配置以避免抛出异常（调用者可根据需要处理）
                return new ApiClientCfg();
            }
        }
    }
}
