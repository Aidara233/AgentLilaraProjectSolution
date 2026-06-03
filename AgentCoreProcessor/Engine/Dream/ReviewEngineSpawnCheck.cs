using System.Threading.Tasks;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// ReviewEngine 的 SpawnCheck。直接响应 force-review 信号启动 Review，
    /// 不经过 DreamEngine。DreamEngine 仅负责自动触发（间隔+空闲检查）。
    /// </summary>
    internal class ReviewEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Review";

        private string? _forcedMode;

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            if (e is SignalEvent signal)
            {
                switch (signal.SignalName)
                {
                    case "force-review:beacon":
                        _forcedMode = "beacon";
                        break;
                    case "force-review:candidate":
                        _forcedMode = "candidate";
                        break;
                }
            }
            return Task.CompletedTask;
        }

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            if (_forcedMode == null)
                return Task.FromResult(false);

            if (ctx.HasActiveEngine("Review"))
            {
                Signal.Event(LogGroup.Engine, "Review跳过", new { reason = "已有活跃Review引擎" });
                _forcedMode = null;
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            var mode = _forcedMode;
            _forcedMode = null;
            var engine = new ReviewEngine(ctx);
            engine.ForceSeedMode(mode!);
            Signal.Event(LogGroup.Engine, "Review.SpawnCheck创建", new { mode });
            return engine;
        }
    }
}
