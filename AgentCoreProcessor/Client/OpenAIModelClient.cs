using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using OpenAI;
using OpenAI.Chat;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
        private CacheCaptureHandler? _cacheHandler;
        private readonly List<ChatMessage> _pendingToolMessages = new();

        public OpenAIModelClient() : base() { }
        public OpenAIModelClient(ApiClientCfg cfg) : base(cfg) { }

        private ChatClient GetOrCreateChatClient()
        {
            if (_chatClient != null) return _chatClient;

            _cacheHandler = new CacheCaptureHandler();
            var httpClient = new HttpClient(_cacheHandler);
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            var credential = new ApiKeyCredential(apiClientCfg.ApiKey);
            var options = new OpenAIClientOptions
            {
                Transport = new HttpClientPipelineTransport(httpClient)
            };

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
            Models.Usage? lastUsage = null;

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
                    lastUsage = new Models.Usage
                    {
                        PromptTokens = update.Usage.InputTokenCount,
                        CompletionTokens = update.Usage.OutputTokenCount,
                        TotalTokens = update.Usage.TotalTokenCount,
                        PromptCacheHitTokens = update.Usage.InputTokenDetails?.CachedTokenCount ?? 0
                    };
                    var usageResp = BuildSyntheticResponse(null, null);
                    usageResp.Usage = lastUsage;
                    onDelta(usageResp);
                }
            }

            // 从原始 SSE 补充 DeepSeek prompt_cache_hit_tokens（SDK 不解析此字段）
            SupplementCacheTokens(lastUsage);

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
            Models.Usage? lastUsage = null;

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
                    lastUsage = new Models.Usage
                    {
                        PromptTokens = update.Usage.InputTokenCount,
                        CompletionTokens = update.Usage.OutputTokenCount,
                        TotalTokens = update.Usage.TotalTokenCount,
                        PromptCacheHitTokens = update.Usage.InputTokenDetails?.CachedTokenCount ?? 0
                    };
                    onEvent(new StreamEvent
                    {
                        Type = StreamEventType.Usage,
                        Usage = lastUsage
                    });
                }
            }

            // 从原始 SSE 补充 DeepSeek prompt_cache_hit_tokens（SDK 不解析此字段）
            SupplementCacheTokens(lastUsage);
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
            _cacheHandler?.Dispose();
            _cacheHandler = null;
            _chatClient = null;
            base.Dispose();
        }

        /// <summary>
        /// 从原始 SSE 响应中提取 DeepSeek prompt_cache_hit/miss_tokens 补充到 Usage。
        /// OpenAI SDK 不解析这些 DeepSeek 特有字段。
        /// </summary>
        private void SupplementCacheTokens(Models.Usage? usage)
        {
            if (usage == null) return;
            var tee = CacheCaptureHandler.CurrentTee.Value;
            if (tee?.Captured == null || tee.Captured.Length == 0) return;

            try
            {
                var text = Encoding.UTF8.GetString(tee.Captured);
                var (hitTokens, missTokens) = ParseCacheTokensFromSse(text);
                if (hitTokens > 0) usage.PromptCacheHitTokens = hitTokens;
                if (missTokens > 0) usage.PromptCacheMissTokens = missTokens;
            }
            catch { }
            finally
            {
                CacheCaptureHandler.CurrentTee.Value = null;
            }
        }

        /// <summary>从原始 SSE 文本中提取 prompt_cache_hit/miss_tokens，取最后一个匹配。</summary>
        private static (int hitTokens, int missTokens) ParseCacheTokensFromSse(string rawSse)
        {
            int hitTokens = 0, missTokens = 0;
            try
            {
                var hitMatches = Regex.Matches(rawSse, @"prompt_cache_hit_tokens["":\s]+(\d+)");
                if (hitMatches.Count > 0)
                    _ = int.TryParse(hitMatches[^1].Groups[1].Value, out hitTokens);

                var missMatches = Regex.Matches(rawSse, @"prompt_cache_miss_tokens["":\s]+(\d+)");
                if (missMatches.Count > 0)
                    _ = int.TryParse(missMatches[^1].Groups[1].Value, out missTokens);
            }
            catch { }
            return (hitTokens, missTokens);
        }

        /// <summary>
        /// 将读取的字节同时写入 MemoryStream，用于捕获原始 SSE 响应。
        /// </summary>
        private class TeeStream : Stream
        {
            private readonly Stream _inner;
            private readonly MemoryStream _captured = new();
            private bool _disposed;

            public byte[] Captured => _captured.ToArray();

            public TeeStream(Stream inner) => _inner = inner;

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int bytes = _inner.Read(buffer, offset, count);
                if (bytes > 0) _captured.Write(buffer, offset, bytes);
                return bytes;
            }

            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            {
                int bytes = await _inner.ReadAsync(buffer, offset, count, ct).ConfigureAwait(false);
                if (bytes > 0) await _captured.WriteAsync(buffer, offset, bytes, ct).ConfigureAwait(false);
                return bytes;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            {
                int bytes = await _inner.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (bytes > 0) _captured.Write(buffer.Span[..bytes]);
                return bytes;
            }

            public override void Flush() => _inner.Flush();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (!_disposed && disposing)
                {
                    _inner.Dispose();
                    _captured.Dispose();
                    _disposed = true;
                }
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// 将 HTTP 响应流包装为 TeeStream，以便捕获原始 SSE 数据。
        /// 使用 AsyncLocal 确保线程安全。
        /// </summary>
        private class CacheCaptureHandler : HttpClientHandler
        {
            internal static readonly AsyncLocal<TeeStream?> CurrentTee = new();

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.Content != null)
                {
                    var originalStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var tee = new TeeStream(originalStream);
                    CurrentTee.Value = tee;

                    var newContent = new StreamContent(tee);
                    if (response.Content.Headers.ContentType != null)
                        newContent.Headers.ContentType = response.Content.Headers.ContentType;
                    foreach (var (key, values) in response.Content.Headers)
                    {
                        if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                            continue;
                        newContent.Headers.TryAddWithoutValidation(key, values);
                    }
                    response.Content = newContent;
                }

                return response;
            }
        }
    }
}
