using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Memory;

namespace AgentCoreProcessor.Engine
{
    public enum WorkerState
    {
        Pending,
        Classifying,
        Processing,
        Expressing,
        Completed,
        Failed
    }

    internal class WorkerEngine
    {
        private readonly IncomingMessage message;
        private readonly AdapterManager adapterManager;
        private readonly SessionContext context;
        private readonly MemoryService? memorySvc;

        private readonly PreprocessingCore preprocessingCore = new();
        private readonly ExpressCore expressCore = new();
        private readonly WorkingCore workingCore = new();

        private WorkerState state = WorkerState.Pending;

        public WorkerState State => state;

        public WorkerEngine(IncomingMessage message, AdapterManager adapterManager,
            SessionContext context, MemoryService? memorySvc = null)
        {
            this.message = message;
            this.adapterManager = adapterManager;
            this.context = context;
            this.memorySvc = memorySvc;
        }

        /// <summary>
        /// 执行消息处理流程。
        /// 返回 null 表示已通过说话工具实时回复，MasterEngine 无需再推送。
        /// </summary>
        public async Task<string?> RunAsync()
        {
            try
            {
                // 1. 分类
                state = WorkerState.Classifying;
                var category = await preprocessingCore.ClassifyAsync(message.Content);

                // 2. 检索相关记忆，构建 additionalContext
                string? memoryContext = await BuildMemoryContextAsync(
                    includeLinks: category >= 3); // 任务类走完整流程含关联扩展

                // 3. 根据分类路由
                state = WorkerState.Processing;
                switch (category)
                {
                    case 1:
                    case 2:
                        // 简单聊天 → ExpressCore 润色后返回
                        state = WorkerState.Expressing;
                        expressCore.ResetProcessor();
                        var input = memoryContext != null
                            ? $"{message.Content}\n\n[记忆参考]\n{memoryContext}"
                            : message.Content;
                        var expressed = await expressCore.GenerateOnceAsync(input);
                        state = WorkerState.Completed;
                        return expressed;

                    case 3:
                    case 4:
                        // 任务类 → Agent 循环
                        workingCore.OnSpeak = async (rawText) =>
                        {
                            var polished = await expressCore.PolishAsync(message.Content, rawText);
                            await adapterManager.SendMessageAsync(message.Platform, new OutgoingMessage
                            {
                                ChannelId = message.ChannelId,
                                Content = polished
                            });
                        };
                        // 记忆工具回调
                        workingCore.OnMemory = async (content) =>
                        {
                            if (memorySvc != null)
                                await memorySvc.StoreAsync(content,
                                    context.Person.Id, context.Channel.Id, context.Topic.Id);
                        };
                        await workingCore.ProcessAsync(message.Content, memoryContext);
                        state = WorkerState.Completed;
                        return null;

                    default:
                        state = WorkerState.Expressing;
                        expressCore.ResetProcessor();
                        var defaultExpressed = await expressCore.GenerateOnceAsync(message.Content);
                        state = WorkerState.Completed;
                        return defaultExpressed;
                }
            }
            catch (Exception)
            {
                state = WorkerState.Failed;
                throw;
            }
        }

        /// <summary>检索记忆并格式化为上下文文本。</summary>
        private async Task<string?> BuildMemoryContextAsync(bool includeLinks)
        {
            if (memorySvc == null) return null;

            try
            {
                var results = await memorySvc.RecallAsync(
                    context.Person.Id, context.Channel.Id, context.Topic.Id,
                    message.Content, topK: 10, includeLinks: includeLinks);

                if (results.Count == 0) return null;

                var sb = new StringBuilder();
                foreach (var m in results)
                    sb.AppendLine($"- {m.Content}");
                return sb.ToString().TrimEnd();
            }
            catch (Exception)
            {
                return null; // 记忆检索失败不阻塞主流程
            }
        }
    }
}
