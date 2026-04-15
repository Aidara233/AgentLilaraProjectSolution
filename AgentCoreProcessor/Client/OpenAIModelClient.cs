using AgentCoreProcessor.Models;
using OpenAI;
using OpenAI.Chat;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Client
{
    /// <summary>
    /// OpenAI 兼容协议实现（基于官方 OpenAI SDK，支持 DeepSeek、SiliconFlow 等）。
    /// </summary>
    public class OpenAIModelClient : ModelClientBase
    {
        private ChatClient? _chatClient;

        public OpenAIModelClient() : base() { }
        public OpenAIModelClient(ApiClientCfg cfg) : base(cfg) { }

        private ChatClient GetOrCreateChatClient()
        {
            if (_chatClient != null) return _chatClient;

            var credential = new ApiKeyCredential(apiClientCfg.ApiKey);
            var options = new OpenAIClientOptions();

            // 自定义端点（SiliconFlow 等）
            if (!string.IsNullOrEmpty(apiClientCfg.ApiEndpoint))
            {
                var endpoint = apiClientCfg.ApiEndpoint.TrimEnd('/');
                // 去掉末尾的 /chat/completions（SDK 会自己拼）
                if (endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
                    endpoint = endpoint[..^"/chat/completions".Length];
                // 去掉末尾的 /v1（SDK 会自己拼）
                if (endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                    endpoint = endpoint[..^"/v1".Length];
                options.Endpoint = new Uri(endpoint);
            }

            _chatClient = new ChatClient(apiClientCfg.Model, credential, options);
            return _chatClient;
        }

        public override async Task<string> StreamChatAsync(Action<ApiResponse> onDelta, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(onDelta);

            var chatClient = GetOrCreateChatClient();
            var history = GetConversationHistory();

            // 转换为 SDK 消息列表
            var messages = new List<ChatMessage>();
            foreach (var msg in history)
            {
                switch (msg.Role)
                {
                    case "system":
                        messages.Add(new SystemChatMessage(msg.Content));
                        break;
                    case "assistant":
                        messages.Add(new AssistantChatMessage(msg.Content));
                        break;
                    default: // user
                        messages.Add(BuildUserMessage(msg));
                        break;
                }
            }

            var options = new ChatCompletionOptions
            {
                Temperature = (float)apiClientCfg.Temperature,
                MaxOutputTokenCount = apiClientCfg.MaxTokens,
            };
            if (apiClientCfg.TopP.HasValue)
                options.TopP = (float)apiClientCfg.TopP.Value;
            if (apiClientCfg.FrequencyPenalty.HasValue)
                options.FrequencyPenalty = (float)apiClientCfg.FrequencyPenalty.Value;
            if (apiClientCfg.PresencePenalty.HasValue)
                options.PresencePenalty = (float)apiClientCfg.PresencePenalty.Value;

            var fullContent = new StringBuilder();

            AsyncCollectionResult<StreamingChatCompletionUpdate> stream =
                chatClient.CompleteChatStreamingAsync(messages, options, ct);

            await foreach (var update in stream)
            {
                // 文本内容
                if (update.ContentUpdate != null && update.ContentUpdate.Count > 0)
                {
                    foreach (var part in update.ContentUpdate)
                    {
                        if (part.Text != null)
                        {
                            fullContent.Append(part.Text);
                            onDelta(BuildSyntheticResponse(part.Text, null));
                        }
                    }
                }

                // usage（仅最后一个 update 有）
                if (update.Usage != null)
                {
                    var usageResp = BuildSyntheticResponse(null, null);
                    usageResp.Usage = new Models.Usage
                    {
                        PromptTokens = update.Usage.InputTokenCount,
                        CompletionTokens = update.Usage.OutputTokenCount,
                        TotalTokens = update.Usage.TotalTokenCount
                    };
                    onDelta(usageResp);
                }
            }

            return fullContent.ToString();
        }

        private static ChatMessage BuildUserMessage(Models.Message msg)
        {
            if (msg.ContentParts != null && msg.ContentParts.Count > 0)
            {
                var parts = new List<ChatMessageContentPart>();
                foreach (var part in msg.ContentParts)
                {
                    if (part.Type == "text" && part.Text != null)
                        parts.Add(ChatMessageContentPart.CreateTextPart(part.Text));
                    else if (part.Type == "image" && !string.IsNullOrEmpty(part.ImagePath))
                    {
                        var imgPart = BuildImagePart(part.ImagePath);
                        if (imgPart != null) parts.Add(imgPart);
                    }
                }
                if (parts.Count > 0)
                    return new UserChatMessage(parts);
            }
            return new UserChatMessage(msg.Content);
        }

        private static ChatMessageContentPart? BuildImagePart(string imagePath)
        {
            try
            {
                if (!File.Exists(imagePath)) return null;
                var bytes = File.ReadAllBytes(imagePath);
                var mediaType = InferMediaType(imagePath);
                return ChatMessageContentPart.CreateImagePart(
                    BinaryData.FromBytes(bytes), mediaType);
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
            // ChatClient 不实现 IDisposable，无需额外清理
            base.Dispose();
        }
    }
}
