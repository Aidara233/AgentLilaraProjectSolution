using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// SystemEngine 的创建条件检查。单例模式：仅在无存活 SystemEngine 时创建。
    /// 响应 SystemEvent.Started 触发。
    /// </summary>
    internal class SystemEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "System";

        private SystemEngine? activeInstance;

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            // 清理已死亡的实例
            if (activeInstance != null && !activeInstance.IsAlive)
            {
                activeInstance = null;
            }
            return Task.CompletedTask;
        }

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            // 只响应 SystemEvent.Started（系统启动时自动创建）
            if (e is not SystemEvent systemEvent || systemEvent.Action != SystemAction.Started)
            {
                return Task.FromResult(false);
            }

            // 单例：已有存活实例则不创建
            if (activeInstance != null && activeInstance.IsAlive)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            var engine = new SystemEngine(ctx);
            activeInstance = engine;
            return engine;
        }
    }
}
