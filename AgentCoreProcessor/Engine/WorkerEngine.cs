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

        private readonly PreprocessingCore preprocessingCore = new();
        private readonly ExpressCore expressCore = new();
        private readonly WorkingCore workingCore = new();

        private WorkerState state = WorkerState.Pending;

        public WorkerState State => state;

        public WorkerEngine(IncomingMessage message, AdapterManager adapterManager)
        {
            this.message = message;
            this.adapterManager = adapterManager;
        }

        public async Task<string> RunAsync()
        {
            try
            {
                // 1. 分类
                state = WorkerState.Classifying;
                var category = await ClassifyAsync(message.Content);

                // 2. 根据分类执行对应行动
                state = WorkerState.Processing;
                string result;
                switch (category)
                {
                    case 1: // 聊天
                        result = await ChatAsync(message.Content);
                        break;
                    case 2: // 需要额外知识（后续接入 MemoryService，当前先走聊天）
                        result = await ChatAsync(message.Content);
                        break;
                    case 3: // 任务
                    case 4: // 大型任务
                        result = await WorkAsync(message.Content);
                        break;
                    default:
                        result = await ChatAsync(message.Content);
                        break;
                }

                // 3. 人格化输出
                state = WorkerState.Expressing;
                var expressed = await ExpressAsync(result);

                state = WorkerState.Completed;
                return expressed;
            }
            catch (Exception)
            {
                state = WorkerState.Failed;
                throw;
            }
        }

        private async Task<int> ClassifyAsync(string content)
        {
            var result = await preprocessingCore.GenerateOnceAsync(content);
            result = result.Trim();

            if (int.TryParse(result, out var category) && category >= 1 && category <= 4)
                return category;

            return 1; // 无法解析时默认为聊天
        }

        private Task<string> ChatAsync(string content)
        {
            // 纯聊天场景：直接传递给 ExpressAsync 人格化，不需要额外的工作步骤
            return Task.FromResult(content);
        }

        private async Task<string> WorkAsync(string content)
        {
            // 调用 WorkingCore 生成工具调用计划
            var result = await workingCore.GenerateOnceAsync(content);

            // TODO: 解析 ToolCall JSON，构建 DAG，执行工具，汇总结果
            // 当前先直接返回 WorkingCore 的原始输出
            return result;
        }

        private async Task<string> ExpressAsync(string content)
        {
            return await expressCore.GenerateOnceAsync(content);
        }
    }
}