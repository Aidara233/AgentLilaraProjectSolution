using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AgentCoreProcessor.Engine;
using AgentCoreProcessor.Logging;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class SystemMonitor : IDisposable
    {
        private readonly MasterEngine engine;
        private readonly AlertService alertService;
        private readonly Timer timer;

        public SystemSnapshot Current { get; private set; } = new();
        public event Action? OnStateChanged;

        public SystemMonitor(MasterEngine engine, AlertService alertService)
        {
            this.engine = engine;
            this.alertService = alertService;
            timer = new Timer(_ => CollectSnapshot(), null, 1000, 2000);
        }

        private void CollectSnapshot()
        {
            try
            {
                var workerCheck = engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
                var dreamCheck = engine.GetSpawnCheck<DreamEngineSpawnCheck>();
                var systemCheck = engine.GetSpawnCheck<SystemEngineSpawnCheck>();

                var workers = new List<WorkerSnapshot>();
                if (workerCheck != null)
                {
                    foreach (var (channelId, w) in workerCheck.GetActiveChannels())
                    {
                        if (!w.IsAlive) continue;
                        var snap = w.GetSnapshot();
                        workers.Add(snap);
                    }
                }

                DreamStateSnapshot? dreamState = null;
                if (dreamCheck != null)
                {
                    bool hasActive = engine.HasActiveEngine("Dream");
                    dreamState = dreamCheck.GetDreamSnapshot(hasActive);
                }

                SystemEngineSnapshot? systemState = null;
                if (systemCheck != null)
                {
                    systemState = systemCheck.GetSystemSnapshot();
                }

                var snapshot = new SystemSnapshot
                {
                    IsIdle = engine.IsIdle,
                    IdleDuration = engine.IdleDuration,
                    LastMessageTime = engine.LastMessageTime,
                    MuteMode = engine.MuteMode,
                    EngineSummary = engine.GetActiveEngineSummary(),
                    Workers = workers,
                    DreamState = dreamState,
                    SystemEngine = systemState,
                    SnapshotTime = DateTime.Now
                };

                Current = new SystemSnapshot
                {
                    IsIdle = snapshot.IsIdle,
                    IdleDuration = snapshot.IdleDuration,
                    LastMessageTime = snapshot.LastMessageTime,
                    MuteMode = snapshot.MuteMode,
                    EngineSummary = snapshot.EngineSummary,
                    Workers = snapshot.Workers,
                    DreamState = snapshot.DreamState,
                    SystemEngine = snapshot.SystemEngine,
                    Alerts = alertService.CollectAlerts(snapshot),
                    SnapshotTime = snapshot.SnapshotTime
                };

                OnStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Signal.Warn(LogGroup.Engine, "系统快照采集失败", new { error = ex.Message });
            }
        }

        public void Dispose()
        {
            timer.Dispose();
        }
    }
}
