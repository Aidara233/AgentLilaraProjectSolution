using Newtonsoft.Json;
using System.Collections.Generic;

namespace AgentCoreProcesser.Models
{
    public class Delta
    {
        // role can be present in delta
        [JsonProperty("role", NullValueHandling = NullValueHandling.Ignore)]
        public string? Role { get; set; }

        // content in streaming delta can be null
        [JsonProperty("content", NullValueHandling = NullValueHandling.Ignore)]
        public string? Content { get; set; }

        // additional reasoning content field used by some models
        [JsonProperty("reasoning_content", NullValueHandling = NullValueHandling.Ignore)]
        public string? ReasoningContent { get; set; }
    }

    public class Choice
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        // legacy / non-streaming message
        [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)]
        public Message? Message { get; set; }

        // streaming delta field (chunk responses)
        [JsonProperty("delta", NullValueHandling = NullValueHandling.Ignore)]
        public Delta? Delta { get; set; }

        [JsonProperty("finish_reason", NullValueHandling = NullValueHandling.Ignore)]
        public string? FinishReason { get; set; }
    }

    public class CompletionTokensDetails
    {
        [JsonProperty("reasoning_tokens", NullValueHandling = NullValueHandling.Ignore)]
        public int? ReasoningTokens { get; set; }
    }

    public class Usage
    {
        [JsonProperty("prompt_tokens")]
        public int PromptTokens { get; set; }

        [JsonProperty("completion_tokens")]
        public int CompletionTokens { get; set; }

        [JsonProperty("total_tokens")]
        public int TotalTokens { get; set; }

        [JsonProperty("completion_tokens_details", NullValueHandling = NullValueHandling.Ignore)]
        public CompletionTokensDetails? CompletionTokensDetails { get; set; }
    }

    public class ApiResponse
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("object")]
        public string Object { get; set; } = "";

        [JsonProperty("created")]
        public long Created { get; set; }

        [JsonProperty("model")]
        public string Model { get; set; } = "";

        [JsonProperty("choices")]
        public List<Choice> Choices { get; set; } = new List<Choice>();

        [JsonProperty("usage", NullValueHandling = NullValueHandling.Ignore)]
        public Usage? Usage { get; set; }

        // new field from example JSON
        [JsonProperty("system_fingerprint", NullValueHandling = NullValueHandling.Ignore)]
        public string? SystemFingerprint { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public ErrorInfo? Error { get; set; }
    }

    public class ErrorInfo
    {
        [JsonProperty("message")]
        public string Message { get; set; } = "";

        [JsonProperty("type")]
        public string Type { get; set; } = "";

        [JsonProperty("code", NullValueHandling = NullValueHandling.Ignore)]
        public string? Code { get; set; }
    }
}