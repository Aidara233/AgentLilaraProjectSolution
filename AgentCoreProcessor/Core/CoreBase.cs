using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Engine.Modules;
using AgentCoreProcessor.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
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

        public async Task<Usage> GenerateAsync(Action<ApiResponse>? onDelta = null, Action<ResponseBlock>? onBreak = null)
        {
            var buffer = new StringBuilder();
            var fullContent = new StringBuilder();
            var reasoningLog = new StringBuilder();
            Usage usage = new();

            await processor.ProcessAsync((response) =>
            {
                var delta = response.Choices is { Count: > 0 } ? response.Choices[0].Delta : null;
                if (delta == null) return;

                bool hasContent = delta.ReasoningContent != null || delta.Content != null;
                if (!hasContent) return;

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
            });

            LogOutput(fullContent.ToString(), reasoningLog.ToString(), usage);
            return usage;
        }

        public async Task<string> GenerateOnceAsync()
        {
            var result = new StringBuilder();
            var reasoningLog = new StringBuilder();
            Usage? usage = null;

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
        /// 将模型输入和输出写入独立日志文件 Storage/Logs/Model/{timestamp}_{CoreName}.log。
        /// 返回日志文件名，供框架日志引用。
        /// </summary>
        protected string LogOutput(string content, string reasoning = "", Usage? usage = null)
        {
            string fileName = "";
            try
            {
                var logDir = Path.Combine(PathConfig.LogPath, "Model");
                Directory.CreateDirectory(logDir);
                fileName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{CoreName}.json";
                var logPath = Path.Combine(logDir, fileName);

                var history = processor.Client.GetConversationHistory();
                var cfg = processor.Client.Config;

                var logEntry = new
                {
                    timestamp = DateTime.Now.ToString("o"),
                    coreName = CoreName,
                    caller = CallerTag,
                    model = cfg.Model,
                    provider = cfg.Provider,
                    inputMessages = history.Count,
                    inputChars = history.Sum(m => m.Content?.Length ?? 0),
                    tools = (string[]?)null,
                    dynamicInput = history.LastOrDefault(m => m.Role == "user")?.Content?.Truncate(2000),
                    output = content.Truncate(5000),
                    thinking = string.IsNullOrEmpty(reasoning) ? null : reasoning.Truncate(3000),
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

                FrameworkLogger.LogModelCall("CoreBase", CoreName, fileName);

                if (CallLogRepo != null && usage != null)
                {
                    _ = CallLogRepo.InsertAsync(new ModelCallLog
                    {
                        Timestamp = DateTime.Now,
                        CoreName = CoreName,
                        Model = cfg.Model,
                        Provider = cfg.Provider,
                        InputTokens = usage.PromptTokens,
                        OutputTokens = usage.CompletionTokens,
                        CacheCreationTokens = usage.CacheCreationInputTokens,
                        CacheReadTokens = usage.CacheReadInputTokens,
                        CacheHitTokens = usage.PromptCacheHitTokens ?? 0,
                        LogFileName = fileName
                    });
                }
            }
            catch
            {
            }
            return fileName;
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
