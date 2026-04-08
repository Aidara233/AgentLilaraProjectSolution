using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using AgentCoreProcesser.Models;

namespace AgentCoreProcesser.Client
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

        // OpenAI-style extra_body 支持：允许用户在请求体中注入额外字段（例如 {"thinking": {"type": "enabled"}}）
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

        // 历史对话记录 - 始终初始化为一个空列表（可序列化）
        [JsonProperty("conversationHistory")]
        public List<Message> ConversationHistory { get; set; } = new List<Message>();

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
