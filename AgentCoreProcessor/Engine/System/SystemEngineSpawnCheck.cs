using System;
using System.Threading.Tasks;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// SystemEngine 的创建条件检查。单例模式：仅在无存活 SystemEngine 时创建。
    /// 响应 SystemEvent.Started 或 TimerEvent（自愈）触发。
    /// </summary>
    internal class SystemEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "System";

        private SystemEngine? activeInstance;
        private DateTime? lastDeathTime;
        private int restartCount;

        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            if (activeInstance != null && !activeInstance.IsAlive)
            {
                if (lastDeathTime == null)
                {
                    lastDeathTime = DateTime.Now;
                    restartCount++;
                }
                activeInstance = null;
            }
            return Task.CompletedTask;
        }

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            // 已有存活实例则不创建
            if (activeInstance != null && activeInstance.IsAlive)
            {
                return Task.FromResult(false);
            }

            // 首次启动：响应 SystemEvent.Started
            if (e is SystemEvent systemEvent && systemEvent.Action == SystemAction.Started)
            {
                return Task.FromResult(true);
            }

            // 自愈：响应 TimerEvent（心跳），实例已死亡时重启
            if (e is TimerEvent && activeInstance == null && lastDeathTime != null)
            {
                // 死亡后至少等 10s 再重启，避免疯狂重启
                var elapsed = (DateTime.Now - lastDeathTime.Value).TotalSeconds;
                if (elapsed >= 10)
                {
                    lastDeathTime = null;
                    return Task.FromResult(true);
                }
            }

            return Task.FromResult(false);
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            var engine = new SystemEngine(ctx);
            activeInstance = engine;
            return engine;
        }

        internal WebUI.Services.SystemEngineSnapshot GetSystemSnapshot()
        {
            if (activeInstance == null || !activeInstance.IsAlive)
            {
                return new WebUI.Services.SystemEngineSnapshot
                {
                    IsAlive = false,
                    RestartCount = restartCount,
                    LastDeathTime = lastDeathTime
                };
            }

            var snap = activeInstance.GetSnapshot();
            return new WebUI.Services.SystemEngineSnapshot
            {
                IsAlive = snap.IsAlive,
                TaskQueueDepth = snap.TaskQueueDepth,
                ActiveSubAgentCount = snap.ActiveSubAgentCount,
                HasPendingSleepRequest = snap.HasPendingSleepRequest,
                SleepRequestId = snap.SleepRequestId,
                SleepScore = snap.SleepScore,
                SleepRequestTime = snap.SleepRequestTime,
                LastHealthCheck = snap.LastHealthCheck,
                SubAgents = snap.SubAgents,
                PinboardEntries = snap.PinboardEntries,
                ThinkingNotes = snap.ThinkingNotes,
                ContextRoundCount = snap.ContextRoundCount,
                HasContextSummary = snap.HasContextSummary,
                ConsecutiveFailures = snap.ConsecutiveFailures,
                TotalErrorCount = snap.TotalErrorCount,
                LastErrorTime = snap.LastErrorTime,
                LastErrorMessage = snap.LastErrorMessage,
                RestartCount = restartCount,
                LastDeathTime = lastDeathTime
            };
        }
        internal WebUI.Services.EngineContextSnapshot? GetContextSnapshot()
            => activeInstance?.GetContextSnapshot();

        internal void ForceWake() => activeInstance?.ForceWake();
        internal void ForceCompress() => activeInstance?.ForceCompress();
    }
}
