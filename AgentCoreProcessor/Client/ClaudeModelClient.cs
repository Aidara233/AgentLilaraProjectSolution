using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using Anthropic.SDK;
using Anthropic.SDK.Common;
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
        private readonly List<(string ToolUseId, string Result, bool IsError, List<string>? ImagePaths)> _pendingToolResults = new();

        public ClaudeModelClient() : base() { }
        public ClaudeModelClient(ApiClientCfg cfg) : base(cfg) { }

        private AnthropicClient GetOrCreateClient()
        {
            if (_client != null) return _client;

            _client = new AnthropicClient(
                new APIAuthentication(apiClientCfg.ApiKey),
                requestInterceptor: new RequestDumpInterceptor());

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

        public override void AddToolResult(string toolUseId, string result, bool isError = false, List<string>? imagePaths = null)
        {
            _pendingToolResults.Add((toolUseId, result, isError, imagePaths));
        }

        /// <summary>构建 MessageParameters，含 tools（如有设置）、pending tool results。</summary>
        private MessageParameters BuildParameters(List<SdkMessage> messages)
        {
            var parameters = new MessageParameters
            {
                Model = apiClientCfg.Model,
                MaxTokens = apiClientCfg.MaxTokens ?? DefaultMaxTokens,
                Messages = messages,
                Stream = true,
                Temperature = (decimal)apiClientCfg.Temperature,
            };

            // 提取 system 消息
            var history = GetConversationHistory();
            var systemParts = history.Where(m => m.Role == "system").Select(m => m.Content).ToList();
            if (systemParts.Count > 0)
                parameters.System = new List<SystemMessage> { new(string.Join("\n\n", systemParts)) };

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

            // 原生工具定义
            if (apiClientCfg.UseNativeTools && tools != null && tools.Count > 0)
            {
                parameters.Tools ??= new List<Anthropic.SDK.Common.Tool>();
                foreach (var td in tools)
                {
                    var func = new Function(td.Name, td.Description, td.Parameters);
                    parameters.Tools.Add(new Anthropic.SDK.Common.Tool(func));
                }
            }

            // Prompt Caching
            if (apiClientCfg.ShouldEnableCaching())
            {
                parameters.PromptCaching = PromptCacheType.FineGrained;
                if (parameters.System != null)
                {
                    foreach (var sysMsg in parameters.System)
                        sysMsg.CacheControl = new CacheControl { TTL = CacheDuration.FiveMinutes };
                }
                if (messages.Count > 0 && messages[0].Role == RoleType.User)
                {
                    var firstContent = messages[0].Content;
                    if (firstContent != null && firstContent.Count > 0)
                        firstContent[^1].CacheControl = new CacheControl { TTL = CacheDuration.FiveMinutes };
                }
            }

            return parameters;
        }

        public override async Task<string> StreamChatAsync(Action<ApiResponse> onDelta, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(onDelta);

            var client = GetOrCreateClient();
            // Anthropic 流式拆在两个事件：message_start(StreamStartMessage.Usage) 含 input+cache，
            // message_delta(resp.Usage) 只含 output。累积合并，只覆盖非零字段。
            var mergedUsage = new Models.Usage();
            var lastSentTotal = -1;

            (List<SdkMessage> messages, string fullContent) = await StreamInternalAsync(client, null, ct, (resp, _) =>
            {
                if (resp.Delta?.Text != null)
                    onDelta(BuildSyntheticResponse(resp.Delta.Text, null));
                if (resp.ContentBlock?.Type == "thinking" && resp.Delta?.Type == "thinking_delta")
                {
                    if (resp.Delta?.Text != null)
                        onDelta(BuildSyntheticResponse(null, resp.Delta.Text));
                }

                // message_start: StreamStartMessage.Usage 含 input_tokens + cache_*
                if (resp.StreamStartMessage?.Usage != null)
                {
                    var su = resp.StreamStartMessage.Usage;
                    if (su.InputTokens > 0) mergedUsage.PromptTokens = su.InputTokens;
                    if (su.CacheCreationInputTokens > 0) mergedUsage.CacheCreationInputTokens = su.CacheCreationInputTokens;
                    if (su.CacheReadInputTokens > 0) mergedUsage.CacheReadInputTokens = su.CacheReadInputTokens;
                }
                // message_delta: resp.Usage 含 output_tokens
                if (resp.Usage != null)
                {
                    var u = resp.Usage;
                    if (u.OutputTokens > 0) mergedUsage.CompletionTokens = u.OutputTokens;
                    mergedUsage.TotalTokens = mergedUsage.PromptTokens + mergedUsage.CompletionTokens;
                }

                // 仅在 TotalTokens 变化时回调，避免 message_delta 重复触发
                if ((resp.Usage != null || resp.StreamStartMessage?.Usage != null)
                    && mergedUsage.TotalTokens != lastSentTotal)
                {
                    lastSentTotal = mergedUsage.TotalTokens;
                    var usageResp = BuildSyntheticResponse(null, null);
                    usageResp.Usage = mergedUsage;
                    onDelta(usageResp);
                }
            });

            return fullContent;
        }

        public override async Task StreamChatWithToolsAsync(Action<StreamEvent> onEvent, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(onEvent);

            var client = GetOrCreateClient();

            string? currentToolUseId = null;
            string? currentToolName = null;
            var toolInputJson = new System.Text.StringBuilder();
            var thinkingText = new System.Text.StringBuilder();
            // 累积 usage：message_start.message.usage 含 input+cache，message_delta.usage 只含 output
            var mergedUsage = new Models.Usage();

            (List<SdkMessage> messages, string fullContent) = await StreamInternalAsync(client, onEvent, ct, (resp, onEvt) =>
            {
                // content_block_start: tool_use
                if (resp.ContentBlock?.Type == "tool_use")
                {
                    // 多工具场景：先 finalize 上一个工具
                    if (currentToolUseId != null)
                    {
                        onEvt(new StreamEvent
                        {
                            Type = StreamEventType.ToolUseEnd,
                            ToolUseId = currentToolUseId,
                            ToolName = currentToolName
                        });
                    }

                    currentToolUseId = resp.ContentBlock.Id;
                    currentToolName = resp.ContentBlock.Name;
                    toolInputJson.Clear();
                    onEvt(new StreamEvent
                    {
                        Type = StreamEventType.ToolUseStart,
                        ToolUseId = currentToolUseId,
                        ToolName = currentToolName
                    });
                    return;
                }

                // content_block_start: thinking
                if (resp.ContentBlock?.Type == "thinking")
                {
                    thinkingText.Clear();
                    return;
                }

                // content_block_delta: input_json_delta
                if (resp.Delta?.Type == "input_json_delta" && resp.Delta.PartialJson != null)
                {
                    toolInputJson.Append(resp.Delta.PartialJson);
                    onEvt(new StreamEvent
                    {
                        Type = StreamEventType.ToolUseDelta,
                        Content = resp.Delta.PartialJson,
                        ToolUseId = currentToolUseId,
                        ToolName = currentToolName
                    });
                    return;
                }

                // content_block_delta: thinking_delta
                if (resp.Delta?.Type == "thinking_delta" && resp.Delta.Text != null)
                {
                    thinkingText.Append(resp.Delta.Text);
                    onEvt(new StreamEvent
                    {
                        Type = StreamEventType.Thinking,
                        Content = resp.Delta.Text
                    });
                    return;
                }

                // content_block_delta: text_delta
                if (resp.Delta?.Type == "text_delta" && resp.Delta.Text != null)
                {
                    onEvt(new StreamEvent
                    {
                        Type = StreamEventType.Text,
                        Content = resp.Delta.Text
                    });
                    return;
                }

                // message_delta: stop_reason=tool_use
                if (resp.Delta?.StopReason == "tool_use" && currentToolUseId != null)
                {
                    onEvt(new StreamEvent
                    {
                        Type = StreamEventType.ToolUseEnd,
                        ToolUseId = currentToolUseId,
                        ToolName = currentToolName
                    });
                    currentToolUseId = null;
                    currentToolName = null;
                    return;
                }

                // message_start: StreamStartMessage.Usage 含 input_tokens + cache_*
                if (resp.StreamStartMessage?.Usage != null)
                {
                    var su = resp.StreamStartMessage.Usage;
                    if (su.InputTokens > 0) mergedUsage.PromptTokens = su.InputTokens;
                    if (su.CacheCreationInputTokens > 0) mergedUsage.CacheCreationInputTokens = su.CacheCreationInputTokens;
                    if (su.CacheReadInputTokens > 0) mergedUsage.CacheReadInputTokens = su.CacheReadInputTokens;
                }
                // message_delta: resp.Usage 含 output_tokens
                if (resp.Usage != null)
                {
                    var u = resp.Usage;
                    if (u.OutputTokens > 0) mergedUsage.CompletionTokens = u.OutputTokens;
                    mergedUsage.TotalTokens = mergedUsage.PromptTokens + mergedUsage.CompletionTokens;
                }
            });

            // 最终 usage 事件
            onEvent(new StreamEvent { Type = StreamEventType.Usage, Usage = mergedUsage });
        }

        /// <summary>
        /// 共享的流式调用核心。构建消息、发送请求，通过 onStreamEvent 回调每个响应。
        /// </summary>
        private async Task<(List<SdkMessage> Messages, string FullContent)> StreamInternalAsync(
            AnthropicClient client,
            Action<StreamEvent>? onToolEvent,
            CancellationToken ct,
            Action<MessageResponse, Action<StreamEvent>> onStreamItem)
        {
            var history = GetConversationHistory();

            // 构造 SDK 消息列表，合并连续同角色消息（Anthropic API 要求交替）
            var messages = new List<SdkMessage>();
            foreach (var msg in history.Where(m => m.Role != "system"))
            {
                var role = msg.Role == "assistant" ? RoleType.Assistant : RoleType.User;
                var content = BuildContentBlocks(msg);

                if (messages.Count > 0 && messages[^1].Role == role)
                {
                    messages[^1].Content ??= new List<ContentBase>();
                    messages[^1].Content.AddRange(content);
                }
                else
                {
                    var sdkMsg = new SdkMessage(role, "placeholder");
                    sdkMsg.Content = content;
                    messages.Add(sdkMsg);
                }
            }

            // 追加 pending tool results 到一条 user 消息的末尾
            if (_pendingToolResults.Count > 0)
            {
                var contentBlocks = new List<ContentBase>();

                foreach (var tr in _pendingToolResults)
                {
                    contentBlocks.Add(new ToolResultContent
                    {
                        ToolUseId = tr.ToolUseId,
                        Content = new List<ContentBase> { new TextContent { Text = tr.Result } },
                        IsError = tr.IsError ? true : null
                    });

                    // 工具结果附带的图片
                    if (tr.ImagePaths != null)
                    {
                        foreach (var path in tr.ImagePaths)
                        {
                            var img = BuildImageContent(new Models.ContentPart { Type = "image", ImagePath = path });
                            if (img != null) contentBlocks.Add(img);
                        }
                    }
                }

                if (messages.Count > 0 && messages[^1].Role == RoleType.User)
                {
                    messages[^1].Content ??= new List<ContentBase>();
                    messages[^1].Content.AddRange(contentBlocks);
                }
                else
                {
                    var trMsg = new SdkMessage(RoleType.User, "tool_results");
                    trMsg.Content = contentBlocks;
                    messages.Add(trMsg);
                }
                _pendingToolResults.Clear();
            }

            var parameters = BuildParameters(messages);

            // 流式处理
            var fullContent = new System.Text.StringBuilder();

            await foreach (var resp in client.Messages.StreamClaudeMessageAsync(parameters, ct))
            {
                if (resp.Delta?.Text != null)
                    fullContent.Append(resp.Delta.Text);

                onStreamItem(resp, evt =>
                {
                    if (onToolEvent != null) onToolEvent(evt);
                });
            }

            return (messages, fullContent.ToString());
        }

        private static List<ContentBase> BuildContentBlocks(Models.Message msg)
        {
            if (msg.ContentParts != null && msg.ContentParts.Count > 0)
            {
                var blocks = new List<ContentBase>();
                foreach (var part in msg.ContentParts)
                {
                    switch (part.Type)
                    {
                        case "text" when part.Text != null:
                            blocks.Add(new TextContent { Text = part.Text });
                            break;
                        case "image":
                            var imgBlock = BuildImageContent(part);
                            if (imgBlock != null) blocks.Add(imgBlock);
                            break;
                        case "tool_use" when part.ToolUseId != null && part.ToolName != null:
                            blocks.Add(new ToolUseContent
                            {
                                Id = part.ToolUseId,
                                Name = part.ToolName,
                                Input = part.ToolInput != null
                                    ? JsonSerializer.Deserialize<JsonNode>(part.ToolInput)
                                    : new JsonObject()
                            });
                            break;
                        case "tool_result" when part.ToolUseId != null:
                            blocks.Add(new ToolResultContent
                            {
                                ToolUseId = part.ToolUseId,
                                Content = new List<ContentBase> { new TextContent { Text = part.Text ?? "" } },
                                IsError = part.IsError == true ? true : null
                            });
                            break;
                    }
                }
                return blocks.Count > 0 ? blocks : [new TextContent { Text = msg.Content }];
            }
            return [new TextContent { Text = msg.Content }];
        }

        private static ImageContent? BuildImageContent(Models.ContentPart part)
        {
            try
            {
                // 优先使用已有的 base64 数据
                if (!string.IsNullOrEmpty(part.ImageBase64))
                {
                    return new ImageContent
                    {
                        Source = new ImageSource
                        {
                            MediaType = part.MediaType ?? "image/png",
                            Data = part.ImageBase64
                        }
                    };
                }

                // 从文件路径读取
                if (!string.IsNullOrEmpty(part.ImagePath) && File.Exists(part.ImagePath))
                {
                    var bytes = File.ReadAllBytes(part.ImagePath);
                    return new ImageContent
                    {
                        Source = new ImageSource
                        {
                            MediaType = InferMediaType(part.ImagePath),
                            Data = Convert.ToBase64String(bytes)
                        }
                    };
                }

                return null;
            }
            catch (Exception ex) { Signal.Warn(LogGroup.Model, "图片加载失败", new { path = part.ImagePath, error = ex.Message }); return null; }
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

    /// <summary>
    /// 请求 dump 拦截器：每次请求写入文件，失败时保留证据。
    /// </summary>
    internal class RequestDumpInterceptor : IRequestInterceptor
    {
        private static readonly string DumpDir = Path.Combine(
            Config.PathConfig.StoragePath, "Logs", "ClaudeRequests");
        private static int _seq;

        public async Task<HttpResponseMessage> InvokeAsync(
            HttpRequestMessage request,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> next,
            CancellationToken ct)
        {
            string? body = null;
            if (request.Content != null)
                body = await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            var seq = Interlocked.Increment(ref _seq);
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            Directory.CreateDirectory(DumpDir);

            var resp = await next(request, ct).ConfigureAwait(false);

            // 只在非 2xx 时 dump
            if (!resp.IsSuccessStatusCode)
            {
                var respBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var dump = $"[{ts}] #{seq} {(int)resp.StatusCode} {request.RequestUri}\n\n" +
                           $"=== REQUEST ===\n{body}\n\n" +
                           $"=== RESPONSE ===\n{respBody}\n";
                var path = Path.Combine(DumpDir, $"{ts}_{seq}_FAIL.txt");
                await File.WriteAllTextAsync(path, dump, ct).ConfigureAwait(false);
            }

            return resp;
        }
    }
}
