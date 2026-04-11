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

        public async Task<string> RunAsync()
        {
            try
            {
                // 1. 分类
                state = WorkerState.Classifying;
                var category = await preprocessingCore.ClassifyAsync(message.Content);

                // 2. 根据分类路由到对应处理
                state = WorkerState.Processing;
                string result;
                switch (category)
                {
                    case 1:
                    case 2:
                        // 简单聊天 → 直接交给 ExpressCore 润色
                        result = message.Content;
                        break;

                    case 3:
                    case 4:
                        // 任务类 → Agent 循环
                        // 设置说话回调：说话工具触发时，经 ExpressCore 润色后实时推送给用户
                        workingCore.OnSpeak = async (rawText) =>
                        {
                            expressCore.ResetProcessor();  // 清除上次调用的历史，保持无状态
                            var expressed = await expressCore.GenerateOnceAsync(rawText);
                            await adapterManager.SendMessageAsync(message.Platform, new OutgoingMessage
                            {
                                ChannelId = message.ChannelId,
                                Content = expressed
                            });
                        };
                        result = await workingCore.ProcessAsync(message.Content);
                        break;

                    default:
                        result = message.Content;
                        break;
                }

                // 3. 最终输出也经 ExpressCore 润色
                state = WorkerState.Expressing;
                expressCore.ResetProcessor();  // 清除说话回调可能留下的历史
                var expressed = await expressCore.GenerateOnceAsync(result);

                state = WorkerState.Completed;
                return expressed;
            }
            catch (Exception)
            {
                state = WorkerState.Failed;
                throw;
            }
        }
    }
}
