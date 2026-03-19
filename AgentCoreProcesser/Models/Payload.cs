//using System;
//using System.Collections.Generic;
//using System.Text.Json.Serialization;

//namespace AgentCoreProcesser.Models
//{
//    public class Payload
//    {
//        [JsonPropertyName("id")]
//        public string Id { get; set; } = string.Empty;

//        [JsonPropertyName("object")]
//        public string Object { get; set; } = string.Empty;

//        [JsonPropertyName("created")]
//        public long Created { get; set; }

//        [JsonPropertyName("model")]
//        public string Model { get; set; } = string.Empty;

//        [JsonPropertyName("choices")]
//        public List<Choice> Choices { get; set; } = new();

//        [JsonPropertyName("system_fingerprint")]
//        public string SystemFingerprint { get; set; } = string.Empty;

//        [JsonPropertyName("usage")]
//        public Usage? Usage { get; set; }
//    }

//    public class Choice
//    {
//        [JsonPropertyName("index")]
//        public int Index { get; set; }

//        [JsonPropertyName("delta")]
//        public Delta? Delta { get; set; }

//        [JsonPropertyName("finish_reason")]
//        public string? FinishReason { get; set; }
//    }

//    internal class Delta
//    {
//        [JsonPropertyName("content")]
//        public string? Content { get; set; }

//        [JsonPropertyName("reasoning_content")]
//        public string? ReasoningContent { get; set; }

//        [JsonPropertyName("role")]
//        public string? Role { get; set; }
//    }

//    public class Usage
//    {
//        [JsonPropertyName("prompt_tokens")]
//        public int PromptTokens { get; set; }

//        [JsonPropertyName("completion_tokens")]
//        public int CompletionTokens { get; set; }

//        [JsonPropertyName("total_tokens")]
//        public int TotalTokens { get; set; }

//        [JsonPropertyName("completion_tokens_details")]
//        public CompletionTokensDetails? CompletionTokensDetails { get; set; }
//    }

//    public class CompletionTokensDetails
//    {
//        [JsonPropertyName("reasoning_tokens")]
//        public int ReasoningTokens { get; set; }
//    }
//}
