using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using OpenAI;
using OpenAI.Chat;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
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
        private readonly List<ChatMessage> _pendingToolMessages = new();

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

        public override void AddToolResult(string toolUseId, string result, bool isError = false)
        {
            _pendingToolMessages.Add(new ToolChatMessage(toolUseId, result));
        }

        public override async Task<string> StreamChatAsync(Action<ApiResponse> onDelta, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(onDelta);

            var chatClient = GetOrCreateChatClient();
            var history = GetConversationHistory();

            // 转换为 SDK 消息列表
            var messages = BuildChatMessages(history);

            var options = BuildChatOptions();

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

        public override async Task StreamChatWithToolsAsync(Action<StreamEvent> onEvent, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(onEvent);

            var chatClient = GetOrCreateChatClient();
            var history = GetConversationHistory();
            var messages = BuildChatMessages(history);

            var options = BuildChatOptions();

            // 添加工具定义
            if (tools != null && tools.Count > 0)
            {
                foreach (var td in tools)
                {
                    options.Tools.Add(ChatTool.CreateFunctionTool(
                        td.Name, td.Description,
                        BinaryData.FromString(td.Parameters.ToJsonString())));
                }
            }

            // 跟踪进行中的 tool call
            var activeToolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Args)>();
            var thinkingText = new StringBuilder();

            AsyncCollectionResult<StreamingChatCompletionUpdate> stream =
                chatClient.CompleteChatStreamingAsync(messages, options, ct);

            await foreach (var update in stream)
            {
                // 文本内容
                if (update.ContentUpdate != null)
                {
                    foreach (var part in update.ContentUpdate)
                    {
                        if (part.Text != null)
                        {
                            onEvent(new StreamEvent { Type = StreamEventType.Text, Content = part.Text });
                        }
                    }
                }

                // 工具调用更新
                if (update.ToolCallUpdates != null)
                {
                    foreach (var tc in update.ToolCallUpdates)
                    {
                        if (!activeToolCalls.TryGetValue(tc.Index, out var active))
                        {
                            active = (tc.ToolCallId, tc.FunctionName, new StringBuilder());
                            activeToolCalls[tc.Index] = active;
                            onEvent(new StreamEvent
                            {
                                Type = StreamEventType.ToolUseStart,
                                ToolUseId = tc.ToolCallId,
                                ToolName = tc.FunctionName
                            });
                        }

                        if (tc.FunctionArgumentsUpdate != null)
                        {
                            var argsStr = tc.FunctionArgumentsUpdate.ToString();
                            active.Args.Append(argsStr);
                            onEvent(new StreamEvent
                            {
                                Type = StreamEventType.ToolUseDelta,
                                Content = argsStr,
                                ToolUseId = active.Id,
                                ToolName = active.Name
                            });
                        }
                    }
                }

                // finish reason = tool_calls → ToolUseEnd
                if (update.FinishReason == ChatFinishReason.ToolCalls)
                {
                    foreach (var (_, active) in activeToolCalls)
                    {
                        onEvent(new StreamEvent
                        {
                            Type = StreamEventType.ToolUseEnd,
                            ToolUseId = active.Id,
                            ToolName = active.Name
                        });
                    }
                    activeToolCalls.Clear();
                }

                // usage
                if (update.Usage != null)
                {
                    onEvent(new StreamEvent
                    {
                        Type = StreamEventType.Usage,
                        Usage = new Models.Usage
                        {
                            PromptTokens = update.Usage.InputTokenCount,
                            CompletionTokens = update.Usage.OutputTokenCount,
                            TotalTokens = update.Usage.TotalTokenCount
                        }
                    });
                }
            }
        }

        private List<ChatMessage> BuildChatMessages(List<Message> history)
        {
            var messages = new List<ChatMessage>();
            foreach (var msg in history)
            {
                switch (msg.Role)
                {
                    case "system":
                        messages.Add(new SystemChatMessage(msg.Content));
                        break;
                    case "assistant":
                        messages.Add(BuildAssistantMessage(msg));
                        break;
                    default: // user
                        messages.Add(BuildUserMessage(msg));
                        break;
                }
            }
            // 追加 pending tool messages
            if (_pendingToolMessages.Count > 0)
            {
                messages.AddRange(_pendingToolMessages);
                _pendingToolMessages.Clear();
            }
            return messages;
        }

        private static ChatMessage BuildAssistantMessage(Message msg)
        {
            if (msg.ContentParts != null && msg.ContentParts.Any(p => p.Type == "tool_use"))
            {
                var toolCalls = msg.ContentParts
                    .Where(p => p.Type == "tool_use" && p.ToolUseId != null && p.ToolName != null)
                    .Select(p => ChatToolCall.CreateFunctionToolCall(
                        p.ToolUseId!, p.ToolName!,
                        BinaryData.FromString(p.ToolInput ?? "{}")))
                    .ToList();
                if (toolCalls.Count > 0)
                    return new AssistantChatMessage(toolCalls);
            }
            return new AssistantChatMessage(msg.Content);
        }

        private ChatCompletionOptions BuildChatOptions()
        {
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
            return options;
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
                    else if (part.Type == "image")
                    {
                        var imgPart = BuildImagePart(part);
                        if (imgPart != null) parts.Add(imgPart);
                    }
                }
                if (parts.Count > 0)
                    return new UserChatMessage(parts);
            }
            return new UserChatMessage(msg.Content);
        }

        private static ChatMessageContentPart? BuildImagePart(Models.ContentPart part)
        {
            try
            {
                // 优先使用已有的 base64 数据
                if (!string.IsNullOrEmpty(part.ImageBase64))
                {
                    var bytes = Convert.FromBase64String(part.ImageBase64);
                    var mediaType = part.MediaType ?? "image/png";
                    return ChatMessageContentPart.CreateImagePart(
                        BinaryData.FromBytes(bytes), mediaType);
                }

                // 从文件路径读取
                if (!string.IsNullOrEmpty(part.ImagePath) && File.Exists(part.ImagePath))
                {
                    var bytes = File.ReadAllBytes(part.ImagePath);
                    var mediaType = InferMediaType(part.ImagePath);
                    return ChatMessageContentPart.CreateImagePart(
                        BinaryData.FromBytes(bytes), mediaType);
                }

                return null;
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
