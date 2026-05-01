using System;
using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// DreamEngine 的创建条件检查。
    /// Phase 8: 简化为信号驱动 + 小睡/走神。大睡决策由 SystemEngine 负责。
    /// </summary>
    internal class DreamEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Dream";

        private static string DreamConfigPath =>
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamConfig.json");

        // ---- 跨周期状态 ----
        private DreamConfig cfg = DreamConfig.Load(
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamConfig.json"));

        private volatile bool forceFlag = false;
        private DateTime? lastDaydreamTime;

        // ShouldSpawn 决定的睡眠级别，供 Create 读取
        private SleepLevel pendingLevel;
        private int pendingMaxFragments;


        public Task OnEventAsync(EngineEvent e, ISystemContext ctx)
        {
            if (e is SignalEvent signal)
            {
                switch (signal.SignalName)
                {
                    case "force-sleep":
                        forceFlag = true;
                        FrameworkLogger.Log("DreamSpawnCheck", "强制睡觉信号");
                        break;
                    case "dream-config":
                        if (signal.Payload is string json)
                        {
                            try
                            {
                                var c = JsonConvert.DeserializeObject<DreamConfig>(json);
                                if (c != null) { cfg = c; cfg.Save(DreamConfigPath); }
                            }
                            catch (Exception ex)
                            {
                                FrameworkLogger.Log("DreamSpawnCheck", $"配置更新失败: {ex.Message}");
                            }
                        }
                        break;
                }
            }

            return Task.CompletedTask;
        }

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            // ① 强制睡觉（来自 SystemEngine 或 /sleep 命令）
            if (forceFlag)
            {
                forceFlag = false;
                pendingLevel = SleepLevel.DeepSleep;
                pendingMaxFragments = cfg.MaxFragmentsPerDeepSleep;
                return Task.FromResult(!ctx.HasActiveEngine("Dream"));
            }

            // 只在 TickEvent 时评估小睡/走神
            if (e is not TimerEvent timer || timer.TimerName != "tick")
                return Task.FromResult(false);

            if (!ctx.IsIdle || ctx.HasActiveEngine("Dream")) return Task.FromResult(false);

            // ② 小睡（空闲时间足够长）
            if (ctx.IdleDuration.TotalSeconds > cfg.NapIdleThreshold)
            {
                pendingLevel = SleepLevel.Nap;
                pendingMaxFragments = cfg.MaxFragmentsPerNap;
                FrameworkLogger.Log("DreamSpawnCheck", $"小睡触发（空闲 {ctx.IdleDuration.TotalSeconds:F0}s）");
                return Task.FromResult(true);
            }

            // ③ 走神（冷却期已过）
            if (lastDaydreamTime == null ||
                (DateTime.Now - lastDaydreamTime.Value).TotalSeconds > cfg.DaydreamCooldown)
            {
                pendingLevel = SleepLevel.Daydream;
                pendingMaxFragments = 1;
                FrameworkLogger.Log("DreamSpawnCheck", "走神触发");
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            return new DreamEngine(ctx, pendingLevel, pendingMaxFragments, this);
        }

        // ---- 供 DreamEngine 实例访问 ----

        internal DreamConfig GetConfig() => cfg;

        internal WebUI.Services.DreamStateSnapshot GetDreamSnapshot(bool hasActiveDream) => new()
        {
            ForceFlag = forceFlag,
            LastDaydreamTime = lastDaydreamTime,
            PendingLevel = pendingLevel,
            HasActiveDream = hasActiveDream
        };

        // ---- 供 DreamEngine 实例回调 ----

        internal void OnDreamCompleted(SleepLevel level, int processed)
        {
            if (level == SleepLevel.Daydream)
            {
                lastDaydreamTime = DateTime.Now;
                FrameworkLogger.Log("DreamSpawnCheck", "走神完成");
            }
            else if (level == SleepLevel.Nap)
            {
                FrameworkLogger.Log("DreamSpawnCheck", $"小睡完成，处理 {processed} 个片段");
            }
            else if (level == SleepLevel.DeepSleep)
            {
                FrameworkLogger.Log("DreamSpawnCheck", $"大睡完成，处理 {processed} 个片段");
            }
        }
    }
}
