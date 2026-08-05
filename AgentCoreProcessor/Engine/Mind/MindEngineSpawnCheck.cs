using System.Threading.Tasks;
using AgentCoreProcessor.Engine.Mind;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 心智引擎 SpawnCheck。响应系统启动信号创建 MindEngine（常驻，AutoStart）。
    /// IEmotionAccess 桥的注册由 MasterEngine 完成（Phase 2，能访问 SimpleServiceProvider）。
    /// </summary>
    internal class MindEngineSpawnCheck : IEngineSpawnCheck
    {
        private MindEngine? _active;

        public string EngineType => "Mind";

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx) => Task.CompletedTask;

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            if (e is SystemEvent se && se.Action == SystemAction.Started)
                return Task.FromResult(true);
            return Task.FromResult(false);
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            var engine = new MindEngine(ctx);
            _active = engine;
            return engine;
        }

        /// <summary>供 MasterEngine 取回已创建的 MindEngine（注册服务桥用）。</summary>
        public MindEngine? Active => _active;
    }
}
