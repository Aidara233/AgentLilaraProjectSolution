using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    internal class TimerEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Timer";

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx) => Task.CompletedTask;

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx) => Task.FromResult(false);

        public ISubEngine Create(ISystemContext ctx) => new TimerEngine(ctx);
    }

    /// <summary>
    /// 心跳引擎。定期发布 TimerEvent("tick")，驱动其他引擎的周期性检查。
    /// </summary>
    internal class TimerEngine : ISubEngine
    {
        public string EngineType => "Timer";
        public bool IsAlive { get; private set; } = true;
        public bool IsInfrastructure => true;

        private readonly ISystemContext ctx;
        private int intervalSeconds = 30;

        public TimerEngine(ISystemContext ctx)
        {
            this.ctx = ctx;
        }

        public async Task RunAsync()
        {
            FrameworkLogger.Log("TimerEngine", $"心跳启动，间隔 {intervalSeconds}s");
            while (IsAlive)
            {
                await Task.Delay(intervalSeconds * 1000);
                if (!IsAlive) break;
                ctx.EventBus.Publish(new TimerEvent { TimerName = "tick" });
            }
            FrameworkLogger.Log("TimerEngine", "心跳停止");
        }

        public void OnEvent(EngineEvent e)
        {
            // 可响应配置变更信号调整间隔
            if (e is SignalEvent signal && signal.SignalName == "timer-interval"
                && signal.Payload is string s && int.TryParse(s, out var val) && val > 0)
            {
                intervalSeconds = val;
                FrameworkLogger.Log("TimerEngine", $"间隔调整为 {intervalSeconds}s");
            }
        }

        public void RequestStop() => IsAlive = false;
    }
}
