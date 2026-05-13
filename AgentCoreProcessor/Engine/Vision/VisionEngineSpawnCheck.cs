using System;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine.Vision
{
    internal class VisionEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Vision";

        private VisionEngine? activeInstance;
        private DateTime? lastDeathTime;

        public VisionEngine? ActiveInstance => activeInstance?.IsAlive == true ? activeInstance : null;

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            if (e is SignalEvent signal && signal.SignalName == "new-image")
                activeInstance?.SignalGate();

            if (activeInstance != null && !activeInstance.IsAlive)
            {
                lastDeathTime ??= DateTime.Now;
                activeInstance = null;
            }

            return Task.CompletedTask;
        }

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            if (activeInstance != null && activeInstance.IsAlive)
                return Task.FromResult(false);

            // 检查配置：如果 OCR 和 Vision 都禁用，不启动引擎
            var config = VisionEngineConfig.Load();
            if (!config.OcrEnabled && !config.VisionEnabled)
                return Task.FromResult(false);

            if (e is TimerEvent timer && timer.TimerName == "tick")
            {
                if (lastDeathTime == null || (DateTime.Now - lastDeathTime.Value).TotalSeconds >= 10)
                {
                    lastDeathTime = null;
                    return Task.FromResult(true);
                }
            }

            return Task.FromResult(false);
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            activeInstance = new VisionEngine(ctx);
            return activeInstance;
        }
    }
}
