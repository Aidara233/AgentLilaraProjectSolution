using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Core
{
    internal abstract class CoreBase
    {
        public string CoreName { get => GetType().Name; }

        /// <summary>由 MasterEngine 初始化时设置，供所有 Core 写入调用日志。</summary>
        internal static ModelCallLogRepository? CallLogRepo { get; set; }

        /// <summary>调用来源标签（如 "Channel:2"、"System"、"SubAgent:3"），由调用方设置。</summary>
        public string? CallerTag { get; set; }

        protected Processor processor;

        protected List<Message> extraMessage = [];

        protected List<string> breakString = ["<over>"];

        /// <summary>子类覆盖为 false 可跳过 Persona 注入（工具性 Core 如分类器、摘要器）。</summary>
        protected virtual bool UsePersona => true;

        public CoreBase()
        {
            processor = new Processor(CoreName, usePersona: UsePersona);
        }

        protected CoreBase(string cfgName)
        {
            processor = new Processor(cfgName, usePersona: UsePersona);
        }

        /// <summary>
        /// 将 extraMessage 注入到 processor，供子类在初始化字段后调用。
        /// </summary>
        protected void ApplyExtraMessages()
        {
            foreach (var msg in extraMessage)
            {
                processor.Client.AddMessage(msg.Role, msg.Content);
            }
        }

        public void ResetProcessor()
        {
            processor = new Processor(CoreName, usePersona: UsePersona);
            ApplyExtraMessages();
        }

        /// <summary>
        /// 使用原生工具调用进行流式生成。
        /// </summary>
        protected async Task<Usage> GenerateWithToolsAsync(
            List<ToolDefinition> toolDefs,
            Action<Models.StreamEvent> onEvent,
            CancellationToken ct = default,
            Action? onRetryReset = null)
        {
            processor.Client.SetTools(toolDefs);
            var reasoningLog = new System.Text.StringBuilder();
            var textLog = new System.Text.StringBuilder();
            var toolCallLog = new List<object>();
            Usage usage = new();

            string? currentToolName = null;
            var currentInputJson = new System.Text.StringBuilder();

            var cfg = processor.Client.Config;
            var msgCount = processor.Client.GetConversationHistory().Count;
            var sw = Stopwatch.StartNew();
            bool firstTokenLogged = false;

            Signal.Debug(LogGroup.Model, "模型请求发出", new
            {
                model = cfg.Model,
                messages_count = msgCount,
                core = CoreName,
                caller = CallerTag,
                mode = "tools"
            });

            using var span = Signal.Open(LogGroup.Model, "模型调用", new
            {
                model = cfg.Model,
                core = CoreName,
                caller = CallerTag
            });

            // 提取事件处理器为 local function，首次和重试共用同一逻辑
            Action<Models.StreamEvent> MakeHandler(bool isRetry) => evt =>
            {
                if (!firstTokenLogged && (
                    evt.Type == Models.StreamEventType.Text ||
                    evt.Type == Models.StreamEventType.Thinking ||
                    evt.Type == Models.StreamEventType.ToolUseStart))
                {
                    firstTokenLogged = true;
                    Signal.Debug(LogGroup.Model, isRetry ? "首token到达(重试)" : "首token到达",
                        new { elapsed_ms = sw.ElapsedMilliseconds });
                }

                if (evt.Type == Models.StreamEventType.Thinking && evt.Content != null)
                    reasoningLog.Append(evt.Content);
                if (evt.Type == Models.StreamEventType.Text && evt.Content != null)
                    textLog.Append(evt.Content);
                if (evt.Type == Models.StreamEventType.Usage && evt.Usage != null)
                    usage = evt.Usage;
                if (evt.Type == Models.StreamEventType.ToolUseStart)
                {
                    currentToolName = evt.ToolName;
                    currentInputJson.Clear();
                }
                if (evt.Type == Models.StreamEventType.ToolUseDelta && evt.Content != null)
                    currentInputJson.Append(evt.Content);
                if (evt.Type == Models.StreamEventType.ToolUseEnd && currentToolName != null)
                {
                    toolCallLog.Add(new { tool = currentToolName, input = currentInputJson.ToString() });
                    currentToolName = null;
                }
                onEvent(evt);
            };

            Exception? callError = null;
            try
            {
                await processor.Client.StreamChatWithToolsAsync(MakeHandler(false), ct);
                LogOutput(BuildOutputContent(textLog, toolCallLog), reasoningLog.ToString(), usage);
            }
            catch (Exception ex)
            {
                callError = ex;
                // 失败也写半截文件日志，但不记 token 到数据库
                LogOutput(BuildOutputContent(textLog, toolCallLog), reasoningLog.ToString(), usage, isError: true);

                cfg = processor.Client.Config;
                Signal.Error(LogGroup.Model, "模型调用失败，准备重试",
                    new { error = ex.Message, elapsed_ms = sw.ElapsedMilliseconds });

                // 重试一次：清空所有半截数据，通知调用方重建处理器
                reasoningLog.Clear();
                textLog.Clear();
                toolCallLog.Clear();
                currentToolName = null;
                currentInputJson.Clear();
                usage = new();
                firstTokenLogged = false;
                callError = null;
                onRetryReset?.Invoke();
                try
                {
                    await processor.Client.StreamChatWithToolsAsync(MakeHandler(true), ct);
                    LogOutput(BuildOutputContent(textLog, toolCallLog), reasoningLog.ToString(), usage);
                }
                catch (Exception retryEx)
                {
                    callError = retryEx;
                    LogOutput(BuildOutputContent(textLog, toolCallLog), reasoningLog.ToString(), usage, isError: true);
                }
            }

            span.SetCloseDetail(new
            {
                elapsed_ms = sw.ElapsedMilliseconds,
                model = cfg.Model,
                caller = CallerTag,
                tokens_in = usage.PromptTokens,
                tokens_out = usage.CompletionTokens,
                cached_tokens = usage.PromptCacheHitTokens ?? usage.CacheReadInputTokens,
                tool_calls = toolCallLog.Count,
                error = callError?.Message
            });

            if (callError != null)
                throw callError;

            return usage;
        }

        private static string BuildOutputContent(StringBuilder textLog, List<object> toolCallLog)
        {
            var sb = new System.Text.StringBuilder();
            if (textLog.Length > 0) sb.Append(textLog);
            if (toolCallLog.Count > 0)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(Newtonsoft.Json.JsonConvert.SerializeObject(toolCallLog, Newtonsoft.Json.Formatting.None));
            }
            return sb.Length > 0 ? sb.ToString() : "[native tools: no output]";
        }

        public async Task<Usage> GenerateAsync(Action<ApiResponse>? onDelta = null, Action<ResponseBlock>? onBreak = null)
        {
            var buffer = new StringBuilder();
            var fullContent = new StringBuilder();
            var reasoningLog = new StringBuilder();
            Usage usage = new();

            var cfg = processor.Client.Config;
            var msgCount = processor.Client.GetConversationHistory().Count;
            var sw = Stopwatch.StartNew();
            bool firstTokenLogged = false;

            Signal.Debug(LogGroup.Model, "模型请求发出", new
            {
                model = cfg.Model,
                messages_count = msgCount,
                core = CoreName,
                caller = CallerTag,
                mode = "stream"
            });

            using var span = Signal.Open(LogGroup.Model, "模型调用", new
            {
                model = cfg.Model,
                core = CoreName,
                caller = CallerTag
            });

            try
            {
                await processor.ProcessAsync((response) =>
                {
                    var delta = response.Choices is { Count: > 0 } ? response.Choices[0].Delta : null;
                    if (delta == null) return;

                    bool hasContent = delta.ReasoningContent != null || delta.Content != null;
                    if (!hasContent) return;

                    if (!firstTokenLogged)
                    {
                        firstTokenLogged = true;
                        Signal.Debug(LogGroup.Model, "首token到达", new { elapsed_ms = sw.ElapsedMilliseconds });
                    }

                    if (delta.ReasoningContent != null)
                        reasoningLog.Append(delta.ReasoningContent);

                    onDelta?.Invoke(response);
                    OnDelta(response);

                    if (response.Usage != null)
                        usage = response.Usage;

                    // break 检测：仅在有 content 且注册了 onBreak 时执行
                    if (delta.Content != null)
                    {
                        fullContent.Append(delta.Content);

                        if (onBreak != null)
                        {
                            buffer.Append(delta.Content);

                            while (true)
                            {
                                var text = buffer.ToString();
                                string? matchedBreak = null;
                                int breakIdx = -1;

                                foreach (var breakStr in breakString)
                                {
                                    var idx = text.IndexOf(breakStr);
                                    if (idx >= 0 && (breakIdx < 0 || idx < breakIdx))
                                    {
                                        breakIdx = idx;
                                        matchedBreak = breakStr;
                                    }
                                }

                                if (matchedBreak == null) break;

                                var blockContent = text[..breakIdx];
                                var remainder = text[(breakIdx + matchedBreak.Length)..];

                                var block = new ResponseBlock(matchedBreak, blockContent);
                                onBreak.Invoke(block);
                                OnBreak(block);

                                buffer.Clear();
                                buffer.Append(remainder);
                            }
                        }
                    }
                },
                default,
                onRetryReset: () =>
                {
                    buffer.Clear();
                    fullContent.Clear();
                    reasoningLog.Clear();
                    firstTokenLogged = false;
                });

                span.SetCloseDetail(new
                {
                    elapsed_ms = sw.ElapsedMilliseconds,
                    model = cfg.Model,
                    caller = CallerTag,
                    tokens_in = usage.PromptTokens,
                    tokens_out = usage.CompletionTokens,
                    cached_tokens = usage.PromptCacheHitTokens ?? usage.CacheReadInputTokens
                });

                LogOutput(fullContent.ToString(), reasoningLog.ToString(), usage);
                return usage;
            }
            catch (Exception ex)
            {
                span.SetCloseDetail(new
                {
                    elapsed_ms = sw.ElapsedMilliseconds,
                    model = cfg.Model,
                    caller = CallerTag,
                    tokens_in = usage.PromptTokens,
                    tokens_out = usage.CompletionTokens,
                    cached_tokens = usage.PromptCacheHitTokens ?? usage.CacheReadInputTokens,
                    error = ex.Message
                });

                LogOutput(fullContent.ToString(), reasoningLog.ToString(), usage, isError: true);
                throw;
            }
        }

        public async Task<string> GenerateOnceAsync()
        {
            var result = new StringBuilder();
            var reasoningLog = new StringBuilder();
            Usage? usage = null;

            try
            {
                await processor.ProcessAsync((response) =>
                {
                    var delta = response.Choices is { Count: > 0 } ? response.Choices[0].Delta : null;
                    if (delta == null) return;

                    if (delta.Content != null)
                        result.Append(delta.Content);
                    if (delta.ReasoningContent != null)
                        reasoningLog.Append(delta.ReasoningContent);
                    if (response.Usage != null)
                        usage = response.Usage;
                },
                default,
                onRetryReset: () =>
                {
                    result.Clear();
                    reasoningLog.Clear();
                });

                var output = result.ToString();
                LogOutput(output, reasoningLog.ToString(), usage);
                return output;
            }
            catch (Exception)
            {
                LogOutput(result.ToString(), reasoningLog.ToString(), usage, isError: true);
                throw;
            }
        }

        public async Task<string> GenerateOnceAsync(string userMessage)
        {
            processor.Client.AddUserMessage(userMessage);
            return await GenerateOnceAsync();
        }

        /// <summary>多模态版本：文本 + 图片路径。</summary>
        public async Task<string> GenerateOnceAsync(string userMessage, List<string> imagePaths)
        {
            processor.Client.AddMultimodalMessage("user", userMessage, imagePaths);
            return await GenerateOnceAsync();
        }

        public virtual void OnDelta(ApiResponse response) { }

        public virtual void OnBreak(ResponseBlock block) { }

        /// <summary>
        /// 将模型输入和输出写入独立日志文件 Storage/Logs/Model/{timestamp}_{CoreName}.json。
        /// isError 时只写文件日志，不记录 token 到数据库。
        /// </summary>
        protected string LogOutput(string content, string reasoning = "", Usage? usage = null, bool isError = false)
        {
            string fileName = "";
            try
            {
                var logDir = Path.Combine(PathConfig.LogPath, "Model");
                Directory.CreateDirectory(logDir);
                var suffix = isError ? "_ERROR" : "";
                fileName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{CoreName}{suffix}.json";
                var logPath = Path.Combine(logDir, fileName);

                var history = processor.Client.GetConversationHistory();
                var cfg = processor.Client.Config;

                // 完整记录系统提示词
                var systemMessages = history.Where(m => m.Role == "system").ToList();
                var systemPrompt = systemMessages.Count > 0
                    ? string.Join("\n\n---\n\n", systemMessages.Select(m => m.Content))
                    : null;
                var systemHash = systemPrompt != null ? ComputeShortHash(systemPrompt) : null;

                // 动态消息：非 system 的全部记录，包含 ContentParts 详情
                var dynamicMessages = history
                    .Where(m => m.Role != "system")
                    .Select(m => m.ContentParts != null && m.ContentParts.Count > 0
                        ? (object)new
                        {
                            role = m.Role,
                            contentParts = m.ContentParts.Select(p =>
                            {
                                object? imageInfo = null;
                                if (!string.IsNullOrEmpty(p.ImagePath) || !string.IsNullOrEmpty(p.ImageBase64))
                                {
                                    imageInfo = new
                                    {
                                        mediaType = p.MediaType ?? "image/png",
                                        path = string.IsNullOrEmpty(p.ImagePath) ? null : p.ImagePath,
                                        base64Length = string.IsNullOrEmpty(p.ImageBase64) ? null : (int?)p.ImageBase64.Length
                                    };
                                }
                                return (object)new
                                {
                                    type = p.Type,
                                    text = p.Text,
                                    toolUseId = p.ToolUseId,
                                    toolName = p.ToolName,
                                    toolInput = p.ToolInput,
                                    isError = p.IsError,
                                    image = imageInfo
                                };
                            })
                        }
                        : new { role = m.Role, content = m.Content })
                    .ToList();

                // 工具列表
                var toolNames = processor.Client.GetTools()
                    ?.Select(t => t.Name).ToArray();

                var logEntry = new
                {
                    timestamp = DateTime.Now.ToString("o"),
                    coreName = CoreName,
                    caller = CallerTag,
                    model = cfg.Model,
                    provider = cfg.Provider,
                    systemPrompt,
                    systemPromptHash = systemHash,
                    tools = toolNames,
                    messages = dynamicMessages,
                    output = content,
                    thinking = string.IsNullOrEmpty(reasoning) ? null : reasoning,
                    error = isError ? true : (bool?)null,
                    usage = usage == null ? null : new
                    {
                        inputTokens = usage.PromptTokens,
                        outputTokens = usage.CompletionTokens,
                        totalTokens = usage.TotalTokens,
                        cacheCreationTokens = usage.CacheCreationInputTokens,
                        cacheReadTokens = usage.CacheReadInputTokens,
                        cacheHitTokens = usage.PromptCacheHitTokens,
                        cacheMissTokens = usage.PromptCacheMissTokens
                    }
                };

                var json = JsonConvert.SerializeObject(logEntry, Formatting.Indented,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
                File.WriteAllText(logPath, json);

                if (CallLogRepo != null)
                {
                    _ = CallLogRepo.InsertAsync(new ModelCallLog
                    {
                        Timestamp = DateTime.Now,
                        CoreName = CoreName,
                        Model = cfg.Model,
                        Provider = cfg.Provider,
                        InputTokens = usage?.PromptTokens ?? 0,
                        OutputTokens = usage?.CompletionTokens ?? 0,
                        CacheCreationTokens = usage?.CacheCreationInputTokens ?? 0,
                        CacheReadTokens = usage?.CacheReadInputTokens ?? 0,
                        CacheHitTokens = usage?.PromptCacheHitTokens ?? 0,
                        IsError = isError,
                        LogFileName = fileName
                    });
                }
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "模型日志写入失败", new { error = ex.Message });
            }
            return fileName;
        }

        private static string ComputeShortHash(string input)
        {
            var bytes = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes)[..12].ToLower();
        }
    }

    public readonly struct ResponseBlock
    {
        public string Name { get; }
        public string Content { get; }

        public ResponseBlock(string name, string content)
        {
            Name = name;
            Content = content;
        }
    }
}
