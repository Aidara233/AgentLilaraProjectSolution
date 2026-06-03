using System;
using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
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
        private bool _clearProgressBeforeSpawn;

        private static string ProgressPath =>
            Path.Combine(PathConfig.StoragePath, "Dream", "ReviewProgress.json");

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            if (e is SignalEvent signal)
            {
                switch (signal.SignalName)
                {
                    case "force-review:beacon":
                        _forcedMode = "beacon";
                        _clearProgressBeforeSpawn = true;
                        break;
                    case "force-review:candidate":
                        _forcedMode = "candidate";
                        _clearProgressBeforeSpawn = true;
                        break;
                    case "force-review:resume":
                        _forcedMode = null;
                        _clearProgressBeforeSpawn = false;
                        break;
                }
            }
            return Task.CompletedTask;
        }

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            if (_forcedMode == null && !_clearProgressBeforeSpawn
                && !(e is SignalEvent signal && signal.SignalName == "force-review:resume"))
                return Task.FromResult(false);

            if (ctx.HasActiveEngine("Review"))
            {
                Signal.Event(LogGroup.Engine, "Review跳过", new { reason = "已有活跃Review引擎" });
                _forcedMode = null;
                _clearProgressBeforeSpawn = false;
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            var mode = _forcedMode;
            var clearProgress = _clearProgressBeforeSpawn;
            _forcedMode = null;
            _clearProgressBeforeSpawn = false;

            if (clearProgress && File.Exists(ProgressPath))
            {
                File.Delete(ProgressPath);
                Signal.Event(LogGroup.Engine, "Review.SpawnCheck清除旧进度", new { });
            }

            var engine = new ReviewEngine(ctx);
            if (mode != null)
                engine.ForceSeedMode(mode);

            Signal.Event(LogGroup.Engine, "Review.SpawnCheck创建", new { mode = mode ?? "resume" });
            return engine;
        }
    }
}
