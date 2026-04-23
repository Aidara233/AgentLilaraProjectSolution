using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Models;
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
                        var text = buffer.ToString();

                        foreach (var breakStr in breakString)
                        {
                            if (!text.Contains(breakStr)) continue;

                            var block = new ResponseBlock(breakStr, text.Replace(breakStr, ""));
                            onBreak.Invoke(block);
                            OnBreak(block);
                            buffer.Clear();
                            break;
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

            LogOutput(fullContent.ToString(), reasoningLog.ToString());
            return usage;
        }

        public async Task<string> GenerateOnceAsync()
        {
            var result = new StringBuilder();
            var reasoningLog = new StringBuilder();

            await processor.ProcessAsync((response) =>
            {
                var delta = response.Choices is { Count: > 0 } ? response.Choices[0].Delta : null;
                if (delta == null) return;

                if (delta.Content != null)
                    result.Append(delta.Content);
                if (delta.ReasoningContent != null)
                    reasoningLog.Append(delta.ReasoningContent);
            },
            default,
            onRetryReset: () =>
            {
                result.Clear();
                reasoningLog.Clear();
            });

            var output = result.ToString();
            LogOutput(output, reasoningLog.ToString());
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
        protected string LogOutput(string content, string reasoning = "")
        {
            string fileName = "";
            try
            {
                var logDir = Path.Combine(PathConfig.LogPath, "Model");
                Directory.CreateDirectory(logDir);
                fileName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{CoreName}.log";
                var logPath = Path.Combine(logDir, fileName);

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{CoreName}]");

                // 记录输入消息
                sb.AppendLine("[Input]");
                var history = processor.Client.GetConversationHistory();
                foreach (var msg in history)
                    sb.AppendLine($"  [{msg.Role}] {msg.Content}");

                if (!string.IsNullOrEmpty(reasoning))
                    sb.AppendLine($"[Thinking] {reasoning}");
                sb.AppendLine($"[Output] {content}");

                File.WriteAllText(logPath, sb.ToString());

                // 同时在框架日志中记录引用
                FrameworkLogger.LogModelCall("CoreBase", CoreName, fileName);
            }
            catch
            {
                // 日志写入不应影响主流程
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
