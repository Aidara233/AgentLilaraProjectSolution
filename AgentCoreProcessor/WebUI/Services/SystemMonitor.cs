using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AgentCoreProcessor.Engine;

namespace AgentCoreProcessor.WebUI.Services
{
    internal class SystemMonitor : IDisposable
    {
        private readonly MasterEngine engine;
        private readonly Timer timer;

        public SystemSnapshot Current { get; private set; } = new();
        public event Action? OnStateChanged;

        public SystemMonitor(MasterEngine engine)
        {
            this.engine = engine;
            timer = new Timer(_ => CollectSnapshot(), null, 1000, 2000);
        }

        private void CollectSnapshot()
        {
            try
            {
                var workerCheck = engine.GetSpawnCheck<ChannelEngineSpawnCheck>();
                var dreamCheck = engine.GetSpawnCheck<DreamEngineSpawnCheck>();

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

                Current = new SystemSnapshot
                {
                    IsIdle = engine.IsIdle,
                    IdleDuration = engine.IdleDuration,
                    LastMessageTime = engine.LastMessageTime,
                    MuteMode = engine.MuteMode,
                    EngineSummary = engine.GetActiveEngineSummary(),
                    Workers = workers,
                    DreamState = dreamState,
                    SnapshotTime = DateTime.Now
                };

                OnStateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("SystemMonitor", $"快照采集异常: {ex.Message}");
            }
        }

        public void Dispose()
        {
            timer.Dispose();
        }
    }
}
