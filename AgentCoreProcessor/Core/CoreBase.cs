using AgentCoreProcessor.Models;
using System;
using System.Collections.Generic;
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

        public CoreBase()
        {
            processor = new Processor(CoreName);
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
            processor = new Processor(CoreName);
            ApplyExtraMessages();
        }

        public async Task<Usage> GenerateAsync(Action<ApiResponse>? onDelta = null, Action<ResponseBlock>? onBreak = null)
        {
            var buffer = new StringBuilder();
            Usage usage = new();

            await processor.ProcessAsync((response) =>
            {
                var delta = response.Choices is { Count: > 0 } ? response.Choices[0].Delta : null;
                if (delta == null) return;

                bool hasContent = delta.ReasoningContent != null || delta.Content != null;
                if (!hasContent) return;

                onDelta?.Invoke(response);
                OnDelta(response);

                if (response.Usage != null)
                    usage = response.Usage;

                // break 检测：仅在有 content 且注册了 onBreak 时执行
                if (delta.Content != null && onBreak != null)
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
            });

            return usage;
        }

        public async Task<string> GenerateOnceAsync()
        {
            var result = new StringBuilder();

            await processor.ProcessAsync((response) =>
            {
                var content = response.Choices is { Count: > 0 } ? response.Choices[0].Delta?.Content : null;
                if (content != null)
                    result.Append(content);
            });

            return result.ToString();
        }

        public async Task<string> GenerateOnceAsync(string userMessage)
        {
            processor.Client.AddUserMessage(userMessage);
            return await GenerateOnceAsync();
        }

        public virtual void OnDelta(ApiResponse response) { }

        public virtual void OnBreak(ResponseBlock block) { }
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
