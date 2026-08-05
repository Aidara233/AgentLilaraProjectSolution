using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AgentLilara.PluginSDK.Services;

namespace AgentCoreProcessor.Engine.Mind
{
    /// <summary>
    /// 提取用的轻量 LLM 调用入口（单轮、无工具、无状态）。
    /// 隔离模型客户端细节：本接口由 MindEngine 装配时用现有模型客户端实现
    /// （参考 AgentCoreProcessor.Client 的 IModelClient / CoreBase.GenerateOnceAsync 的单轮调用模式）。
    /// 返回原始文本；null 或非 JSON 由调用方（ExpressExtractor）容错处理。
    /// </summary>
    internal interface IExtractionLlm
    {
        /// <summary>给定上下文（攒批消息的对话文本），返回提取产物 JSON 原始文本。</summary>
        Task<string?> ExtractClaimsAsync(string context);
    }

    /// <summary>
    /// 提取服务（03-memory-engine.md 产生端）。
    /// 组合 ExpressExtractor（伴随提取：消息攒批 + 空闲补一次，单轮 LLM 调用，不开 session）。
    /// 预留 Resurrector 接入点（复活善后整理，Phase 4 实现）。
    /// </summary>
    internal class ExtractService
    {
        /// <summary>框架事件批量提取使用的默认频道 id（非真实频道）。</summary>
        private const string FrameworkChannelId = "framework";

        /// <summary>伴随提取器。</summary>
        public ExpressExtractor Express { get; }

        /// <summary>
        /// 复活善后整理接入点（Phase 4 实现，先占位）。
        /// 待 Resurrector 类型就绪后替换 object 为具体类型：
        /// 监听 OnCompleted → 从 Completed 存档重建 session → 重定义工具集为记忆工具组
        /// → 注入整理任务跑多轮 → 完成再次归档。
        /// </summary>
        public object? Resurrector { get; set; }

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
            // 空闲补一次：把不足一攒批的尾巴也提取掉
            await Express.TriggerIdleAsync();
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
