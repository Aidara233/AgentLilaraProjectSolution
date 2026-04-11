using System;
using System.Threading.Tasks;
using AgentCoreProcessor.Adapter;
using AgentCoreProcessor.Core;

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

        private readonly PreprocessingCore preprocessingCore = new();
        private readonly ExpressCore expressCore = new();
        private readonly WorkingCore workingCore = new();

        private WorkerState state = WorkerState.Pending;

        public WorkerState State => state;

        public WorkerEngine(IncomingMessage message, AdapterManager adapterManager,
            SessionContext context)
        {
            this.message = message;
            this.adapterManager = adapterManager;
            this.context = context;
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

                // 2. 根据分类路由
                state = WorkerState.Processing;
                switch (category)
                {
                    case 1:
                    case 2:
                        // 简单聊天 → ExpressCore 润色后返回
                        state = WorkerState.Expressing;
                        expressCore.ResetProcessor();
                        var expressed = await expressCore.GenerateOnceAsync(message.Content);
                        state = WorkerState.Completed;
                        return expressed;

                    case 3:
                    case 4:
                        // 任务类 → Agent 循环
                        // 说话工具回调：经 ExpressCore 润色后实时推送给用户
                        workingCore.OnSpeak = async (rawText) =>
                        {
                            expressCore.ResetProcessor();
                            var speakExpressed = await expressCore.GenerateOnceAsync(rawText);
                            await adapterManager.SendMessageAsync(message.Platform, new OutgoingMessage
                            {
                                ChannelId = message.ChannelId,
                                Content = speakExpressed
                            });
                        };
                        await workingCore.ProcessAsync(message.Content);
                        // Agent 循环的用户回复由说话工具负责，这里不再额外推送
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
    }
}
