using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace AgentCoreProcesser.Models
{
    [JsonConverter(typeof(ApiRequestConverter))]
    public class ApiRequest
    {
        [JsonProperty("model")]
        public string Model { get; set; } = "gpt-3.5-turbo";

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
        public override void WriteJson(JsonWriter writer, ApiRequest? value, JsonSerializer serializer)
        {
            if (value == null) { writer.WriteNull(); return; }

            // 先用默认方式序列化（跳过本 converter 避免递归）
            var t = JObject.FromObject(value, new JsonSerializer
            {
                NullValueHandling = NullValueHandling.Ignore,
                ContractResolver = serializer.ContractResolver
            });

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