using System.Text;

namespace AgentCoreProcessor.Engine.Modules
{
    /// <summary>
    /// 任务队列模块。注入任务队列状态到系统循环 prompt。
    /// </summary>
    internal class TaskQueueModule : EngineModule
    {
        public override string Name => "任务队列";
        public override int PromptPriority => 40; // 较高优先级，早于便签板

        private readonly ISystemContext ctx;

        public TaskQueueModule(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public override void Attach(ILoopBus bus)
        {
            // 不需要订阅事件，每轮直接读取 TaskBridge 状态
        }

        public override string? BuildPromptSection(EngineMode mode)
        {
            // 只在 Working 模式注入（系统循环只有 Working 模式）
            if (mode != EngineMode.Working) return null;

            var sb = new StringBuilder("[任务队列]\n");

            // 注意：这里只是显示队列状态，实际任务通过 TaskBridge.TaskReader 读取
            // Phase 1 简化版：显示基本信息
            sb.AppendLine("当前任务队列状态：等待任务到达");
            sb.AppendLine("提示：任务会通过 TaskBridge 自动送达，无需主动查询");

            return sb.ToString();
        }
    }
}
