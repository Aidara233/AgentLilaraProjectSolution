using AgentCoreProcesser.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AgentCoreProcesser.Core
{
    internal abstract class CoreBase
    {
        public string CoreName { get => GetType().Name; }

        public Processor processor;

        public List<Message> extraMessage = [];

        public List<string> breakString = ["<over>"];

        public CoreBase()
        {
            processor = new Processor(CoreName);
            foreach (var msg in extraMessage)
            {
                processor.client.AddMessage(msg.Role, msg.Content);
            }
        }

        public void ResetProcessor()
        {
            processor = new Processor(CoreName);
            foreach (var msg in extraMessage)
            {
                processor.client.AddMessage(msg.Role, msg.Content);
            }
        }

        public async Task<Usage> GenerateAsync(Action<ApiResponse>? onDelta = null, Action<ResponseBlock>? onBreak = null)
        {
            StringBuilder result = new();

            Usage usage = new();

            await processor.ProcessAsync((response) =>
            {
                // 有reasoning content或者content都触发onDelta事件，方便外部监听
                if (response.Choices[0].Delta?.ReasoningContent != null || response.Choices[0].Delta?.Content != null)
                {
                    onDelta?.Invoke(response);
                    OnDelta(response);
                    usage = response.Usage ?? usage;
                }

                // 包含breakString中的任意一个字符串，就触发onBreak事件，并把当前result内容（去掉breakString）作为参数传递，同时清空result继续监听后续内容
                if (onBreak != null && response.Choices[0].Delta?.Content != null)
                {
                    result.Append(response.Choices[0].Delta?.Content);
                    foreach (var breakStr in breakString)
                    {
                        if (result.ToString().Contains(breakStr))
                        {
                            onBreak?.Invoke(new ResponseBlock() { name = breakStr, content = result.ToString().Replace(breakStr, "") });
                            OnBreak(new ResponseBlock() { name = breakStr, content = result.ToString().Replace(breakStr, "") });
                            result.Clear();
                            break;
                        }
                    }
                }
                return;
            });
            return usage;
        }

        public async Task<string> GenerateOnceAsync()
        {
            StringBuilder result = new();
            Usage usage = new();
            await processor.ProcessAsync((response) =>
            {
                if (response.Choices[0].Delta?.Content != null)
                {
                    result.Append(response.Choices[0].Delta?.Content);
                    usage = response.Usage ?? usage;
                }
                return;
            });
            return result.ToString();
        }

        public virtual void OnDelta(ApiResponse response) { }

        public virtual void OnBreak(ResponseBlock block) { }
    }

    public struct ResponseBlock
    {
        public string name;
        public string content;
    }
}
