using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 提取服务（03-memory-engine.md 产生端）。
    /// 组合 ExpressExtractor（伴随提取：消息攒批 + 空闲补一次，单轮 LLM 调用，不开 session）。
    /// 善后复活由 <see cref="Resurrector"/> 独立承担（MindEngine 直接持有，不经本服务）。
    /// </summary>
    internal class ExtractService
    {
        /// <summary>框架事件批量提取使用的默认频道 id（非真实频道）。</summary>
        private const string FrameworkChannelId = "framework";

        /// <summary>伴随提取器。</summary>
        public ExpressExtractor Express { get; }

        public ExtractService(
            IWriterAccess writer,
            IExtractionLlm llm,
            int batchSize = ExpressExtractor.DefaultBatchSize)
        {
            Express = new ExpressExtractor(writer, llm, batchSize);
        }

        /// <summary>
        /// 批量提取入口（框架事件用）。
        /// 把最近消息行送入伴随提取器并触发一次提取。
        /// 每行格式 "发送者: 内容"（支持中文冒号）；无分隔符时按普通用户消息处理。
        /// </summary>
        public async Task ExtractAsync(IEnumerable<string> recentMessages)
        {
            foreach (var line in recentMessages)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var (sender, content) = SplitSenderLine(line);
                await Express.AddMessageAsync(FrameworkChannelId, sender, content);
            }
            // 批量提取是一次性调用，直接清空攒批（不等静默判定）
            await Express.FlushAllAsync();
        }

        /// <summary>拆分 "发送者: 内容" 行（支持中英文冒号）。</summary>
        private static (string sender, string content) SplitSenderLine(string line)
        {
            int index = line.IndexOfAny(new[] { ':', '：' });
            if (index > 0)
            {
                var sender = line[..index].Trim();
                var content = line[(index + 1)..].Trim();
                if (sender.Length > 0 && content.Length > 0)
                    return (sender, content);
            }
            return ("用户", line.Trim());
        }
    }
}
