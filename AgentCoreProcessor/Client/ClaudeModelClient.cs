using AgentCoreProcessor.Models;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

// 避免歧义的别名
using SdkMessage = Anthropic.SDK.Messaging.Message;
using SdkDelta = Anthropic.SDK.Messaging.Delta;

namespace AgentCoreProcessor.Client
{
    /// <summary>
    /// Claude 原生 Messages API 实现（基于 Anthropic.SDK）。
    /// </summary>
    public class ClaudeModelClient : ModelClientBase
    {
        private const int DefaultMaxTokens = 4096;
        private AnthropicClient? _client;

        public ClaudeModelClient() : base() { }
        public ClaudeModelClient(ApiClientCfg cfg) : base(cfg) { }

        private AnthropicClient GetOrCreateClient()
        {
            if (_client != null) return _client;

            _client = new AnthropicClient(new APIAuthentication(apiClientCfg.ApiKey));

            // 自定义端点（中转站）
            if (!string.IsNullOrEmpty(apiClientCfg.ApiEndpoint))
            {
                var baseUrl = apiClientCfg.ApiEndpoint.TrimEnd('/');
                if (baseUrl.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase))
                    baseUrl = baseUrl[..^"/v1/messages".Length];
                _client.ApiUrlFormat = baseUrl + "/{0}/{1}";
            }

            if (!string.IsNullOrEmpty(apiClientCfg.AnthropicVersion))
                _client.AnthropicVersion = apiClientCfg.AnthropicVersion;

            return _client;
        }

        public override async Task<string> StreamChatAsync(Action<ApiResponse> onDelta, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(onDelta);

            var client = GetOrCreateClient();
            var history = GetConversationHistory();

            // 提取 system 消息
            var systemParts = history.Where(m => m.Role == "system").Select(m => m.Content).ToList();
            var systemList = systemParts.Count > 0
                ? new List<SystemMessage> { new(string.Join("\n\n", systemParts)) }
                : null;

            // 构造 SDK 消息列表
            var messages = new List<SdkMessage>();
            foreach (var msg in history.Where(m => m.Role != "system"))
            {
                var role = msg.Role == "assistant" ? RoleType.Assistant : RoleType.User;
                var sdkMsg = new SdkMessage(role, "placeholder");
                sdkMsg.Content = BuildContentBlocks(msg);
                messages.Add(sdkMsg);
            }

            var parameters = new MessageParameters
            {
                Model = apiClientCfg.Model,
                MaxTokens = apiClientCfg.MaxTokens ?? DefaultMaxTokens,
                Messages = messages,
                Stream = true,
                Temperature = (decimal)apiClientCfg.Temperature,
            };

            if (systemList != null)
                parameters.System = systemList;
            if (apiClientCfg.TopP.HasValue)
                parameters.TopP = (decimal)apiClientCfg.TopP.Value;

            // ExtraBody: thinking 配置等
            if (apiClientCfg.ExtraBody != null &&
                apiClientCfg.ExtraBody.TryGetValue("thinking", out var thinkingObj) &&
                thinkingObj is Newtonsoft.Json.Linq.JObject jThinking)
            {
                var thinkingType = jThinking["type"]?.ToString();
                if (thinkingType == "enabled")
                {
                    var budget = jThinking["budget_tokens"]?.ToObject<int>() ?? 10000;
                    parameters.Thinking = new ThinkingParameters { BudgetTokens = budget };
                }
            }

            var fullContent = new System.Text.StringBuilder();
            int inputTokens = 0, outputTokens = 0;

            await foreach (var resp in client.Messages.StreamClaudeMessageAsync(parameters, ct))
            {
                if (resp.Usage != null)
                {
                    if (resp.Usage.InputTokens > 0) inputTokens = resp.Usage.InputTokens;
                    if (resp.Usage.OutputTokens > 0) outputTokens = resp.Usage.OutputTokens;
                }

                if (resp.Delta?.Text != null)
                {
                    fullContent.Append(resp.Delta.Text);
                    onDelta(BuildSyntheticResponse(resp.Delta.Text, null));
                }

                // thinking 块
                if (resp.ContentBlock?.Type == "thinking" && resp.Delta?.Type == "thinking_delta")
                {
                    var thinking = resp.Delta?.Text;
                    if (thinking != null)
                        onDelta(BuildSyntheticResponse(null, thinking));
                }
            }

            // 最终 usage
            var usageResp = BuildSyntheticResponse(null, null);
            usageResp.Usage = new Models.Usage
            {
                PromptTokens = inputTokens,
                CompletionTokens = outputTokens,
                TotalTokens = inputTokens + outputTokens
            };
            onDelta(usageResp);

            return fullContent.ToString();
        }

        private static List<ContentBase> BuildContentBlocks(Models.Message msg)
        {
            if (msg.ContentParts != null && msg.ContentParts.Count > 0)
            {
                var blocks = new List<ContentBase>();
                foreach (var part in msg.ContentParts)
                {
                    if (part.Type == "text" && part.Text != null)
                        blocks.Add(new TextContent { Text = part.Text });
                    else if (part.Type == "image" && !string.IsNullOrEmpty(part.ImagePath))
                    {
                        var imgBlock = BuildImageContent(part.ImagePath);
                        if (imgBlock != null) blocks.Add(imgBlock);
                    }
                }
                return blocks.Count > 0 ? blocks : [new TextContent { Text = msg.Content }];
            }
            return [new TextContent { Text = msg.Content }];
        }

        private static ImageContent? BuildImageContent(string imagePath)
        {
            try
            {
                if (!File.Exists(imagePath)) return null;
                var bytes = File.ReadAllBytes(imagePath);
                var base64 = Convert.ToBase64String(bytes);
                return new ImageContent
                {
                    Source = new ImageSource
                    {
                        MediaType = InferMediaType(imagePath),
                        Data = base64
                    }
                };
            }
            catch { return null; }
        }

        private static string InferMediaType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/png"
            };
        }

        private static ApiResponse BuildSyntheticResponse(string? content, string? reasoning)
        {
            return new ApiResponse
            {
                Choices = new List<Choice>
                {
                    new()
                    {
                        Index = 0,
                        Delta = new Models.Delta
                        {
                            Content = content,
                            ReasoningContent = reasoning
                        }
                    }
                }
            };
        }

        public override void Dispose()
        {
            _client?.Dispose();
            base.Dispose();
        }
    }
}
