using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace AgentCoreProcessor.Models
{
    [JsonConverter(typeof(ApiRequestConverter))]
    public class ApiRequest
    {
        [JsonProperty("model")]
        public string Model { get; set; } = "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B";

        [JsonProperty("messages")]
        public List<Message> Messages { get; set; } = new List<Message>();

        [JsonProperty("temperature", NullValueHandling = NullValueHandling.Ignore)]
        public double? Temperature { get; set; }

        [JsonProperty("max_tokens", NullValueHandling = NullValueHandling.Ignore)]
        public int? MaxTokens { get; set; }

        [JsonProperty("top_p", NullValueHandling = NullValueHandling.Ignore)]
        public double? TopP { get; set; }

        [JsonProperty("frequency_penalty", NullValueHandling = NullValueHandling.Ignore)]
        public double? FrequencyPenalty { get; set; }

        [JsonProperty("presence_penalty", NullValueHandling = NullValueHandling.Ignore)]
        public double? PresencePenalty { get; set; }

        [JsonProperty("stream", NullValueHandling = NullValueHandling.Ignore)]
        public bool? Stream { get; set; }

        [JsonProperty("n", NullValueHandling = NullValueHandling.Ignore)]
        public int? N { get; set; }

        /// <summary>
        /// 额外字段，序列化时会合并到请求体根层（而非嵌套为 "extra_body"）。
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, object>? ExtraBody { get; set; } = null;
    }

    /// <summary>
    /// 自定义序列化：将 ExtraBody 的键值对平铺到 JSON 根层。
    /// </summary>
    internal class ApiRequestConverter : JsonConverter<ApiRequest>
    {
        private static bool _isSerializing;

        public override bool CanWrite => !_isSerializing;

        public override void WriteJson(JsonWriter writer, ApiRequest? value, JsonSerializer serializer)
        {
            if (value == null) { writer.WriteNull(); return; }

            // 防止递归：临时禁用本 converter
            _isSerializing = true;
            try
            {
                var t = JObject.FromObject(value, serializer);

                // 将 ExtraBody 的内容合并到根层
                if (value.ExtraBody is { Count: > 0 })
                {
                    foreach (var kv in value.ExtraBody)
                    {
                        t[kv.Key] = JToken.FromObject(kv.Value, serializer);
                    }
                }

                t.WriteTo(writer);
            }
            finally
            {
                _isSerializing = false;
            }
        }

        public override ApiRequest? ReadJson(JsonReader reader, System.Type objectType, ApiRequest? existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            // 反序列化不需要特殊处理，走默认逻辑
            var obj = JObject.Load(reader);
            var request = new ApiRequest();
            serializer.Populate(obj.CreateReader(), request);
            return request;
        }
    }
}