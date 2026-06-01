using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Logging;
using AgentCoreProcessor.WebUI.Services;
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
        private volatile SleepLevel forcedLevel = SleepLevel.DeepSleep;
        private DateTime? lastDaydreamTime;
        private DateTime? lastNapTime;

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
                        forcedLevel = (signal.Payload as string) switch
                        {
                            "nap" => SleepLevel.Nap,
                            "daydream" => SleepLevel.Daydream,
                            _ => SleepLevel.DeepSleep
                        };
                        break;
                    case "force-wake":
                        activeDreamEngine?.ForceWake(signal.Payload as string ?? "signal");
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
                                Signal.Warn(LogGroup.Engine, "梦境配置信号解析失败", new { error = ex.Message });
                            }
                        }
                        break;
                }
            }

            return Task.CompletedTask;
        }

        public Task<bool> ShouldSpawnAsync(EngineEvent e, ISystemContext ctx)
        {
            // ① 强制睡觉（来自 SystemEngine 或 WebUI）
            if (forceFlag)
            {
                forceFlag = false;
                pendingLevel = forcedLevel;
                pendingMaxFragments = forcedLevel switch
                {
                    SleepLevel.Daydream => 1,
                    SleepLevel.Nap => cfg.MaxFragmentsPerNap,
                    _ => cfg.MaxFragmentsPerDeepSleep
                };
                return Task.FromResult(!ctx.HasActiveEngine("Dream"));
            }

            // 只在 TickEvent 时评估小睡/走神
            if (e is not TimerEvent timer || timer.TimerName != "tick")
                return Task.FromResult(false);

            if (!ctx.IsIdle || ctx.HasActiveEngine("Dream")) return Task.FromResult(false);

            // ② 小睡（空闲时间足够长 + 冷却期已过）
            if (ctx.IdleDuration.TotalSeconds > cfg.NapIdleThreshold
                && (lastNapTime == null || (DateTime.Now - lastNapTime.Value).TotalSeconds > cfg.NapCooldown))
            {
                pendingLevel = SleepLevel.Nap;
                pendingMaxFragments = cfg.MaxFragmentsPerNap;
                return Task.FromResult(true);
            }

            // ③ 走神（需启用 + 空闲足够长 + 冷却期已过）
            if (cfg.DaydreamEnabled
                && ctx.IdleDuration.TotalSeconds > cfg.DaydreamIdleThreshold
                && (lastDaydreamTime == null ||
                (DateTime.Now - lastDaydreamTime.Value).TotalSeconds > cfg.DaydreamCooldown))
            {
                pendingLevel = SleepLevel.Daydream;
                pendingMaxFragments = 1;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            var engine = new DreamEngine(ctx, pendingLevel, pendingMaxFragments, this);
            activeDreamEngine = engine;
            return engine;
        }

        // ---- 供 DreamEngine 实例访问 ----

        private DreamEngine? activeDreamEngine;

        internal DreamConfig GetConfig() => cfg;

        internal WebUI.Services.DreamStateSnapshot GetDreamSnapshot(bool hasActiveDream)
        {
            var active = hasActiveDream ? activeDreamEngine : null;
            var lastRec = active?.LastCompletedRecord;
            return new()
            {
                ForceFlag = forceFlag,
                LastDaydreamTime = lastDaydreamTime,
                PendingLevel = pendingLevel,
                HasActiveDream = hasActiveDream,
                CurrentFragment = active?.CurrentPhase,
                FragmentsCompleted = active?.StepsCompleted ?? 0,
                FragmentsTotal = active?.StepsTotal ?? 0,
                CurrentInputDescription = active?.CurrentInputDescription,
                LastFragmentType = lastRec?.Type,
                LastFragmentSummary = lastRec?.Summary,
                LastFragmentDetails = lastRec?.Details.Select(d => new WebUI.Services.FragmentDetailSnapshot
                {
                    Action = d.Action,
                    MemoryId = d.MemoryId,
                    OldValue = d.OldValue,
                    NewValue = d.NewValue,
                    Note = d.Note
                }).ToList(),
                CompletedFragments = active?.CompletedFragments.ToList(),
                MainBudget = cfg.MainTokenBudget,
                ReserveBudget = cfg.ReserveTokenBudget,
            };
        }

        // ---- 供 DreamEngine 实例回调 ----

        internal void OnDreamCompleted(SleepLevel level, int processed)
        {
            activeDreamEngine = null;
            if (level == SleepLevel.Daydream)
            {
                lastDaydreamTime = DateTime.Now;
            }
            else if (level == SleepLevel.Nap)
            {
                lastNapTime = DateTime.Now;
            }
            else if (level == SleepLevel.DeepSleep)
            {
            }
        }
    }
}
