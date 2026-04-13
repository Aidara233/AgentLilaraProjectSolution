using System;
using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// DreamEngine 的创建条件检查。持有所有跨睡眠周期的状态和做梦调度逻辑。
    /// </summary>
    internal class DreamEngineSpawnCheck : IEngineSpawnCheck
    {
        public string EngineType => "Dream";

        private static string DreamConfigPath =>
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamConfig.json");
        private static string DreamStatsPath =>
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamStats.json");

        // ---- 跨周期状态 ----
        private DreamConfig cfg = DreamConfig.Load(
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamConfig.json"));
        private DreamStats stats = DreamStats.Load(
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamStats.json"));

        private volatile float scoreOffset = 0f;
        private volatile bool customRedAlert = false;
        private volatile bool forceFlag = false;
        private bool dreamPermission = false;
        private DateTime? permissionRequestTime;
        private DateTime? lastDaydreamTime;
        private DateTime? lastDeepSleepTime;

        // ShouldSpawn 决定的睡眠级别，供 Create 读取
        private SleepLevel pendingLevel;
        private int pendingMaxFragments;


        public void OnEvent(EngineEvent e, ISystemContext ctx)
        {
            if (e is SignalEvent signal)
            {
                switch (signal.SignalName)
                {
                    case "dream-permission":
                        dreamPermission = true;
                        FrameworkLogger.Log("DreamSpawnCheck", "睡眠许可已授予");
                        break;
                    case "force-sleep":
                        forceFlag = true;
                        FrameworkLogger.Log("DreamSpawnCheck", "强制睡觉");
                        break;
                    case "sleep-score-offset":
                        if (signal.Payload is string s && float.TryParse(s, out var v))
                        {
                            scoreOffset = v;
                            FrameworkLogger.Log("DreamSpawnCheck", $"睡意偏移: {v}");
                            if (v < 0 && permissionRequestTime != null)
                            {
                                permissionRequestTime = null;
                                FrameworkLogger.Log("DreamSpawnCheck", "负偏移，解除锁定");
                            }
                        }
                        break;
                    case "red-alert":
                        customRedAlert = true;
                        FrameworkLogger.Log("DreamSpawnCheck", "红色警报");
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

            // TickEvent 时更新统计快照
            if (e is TimerEvent timer && timer.TimerName == "tick")
            {
                _ = UpdateSnapshotAsync(ctx);
            }
        }

        private async Task UpdateSnapshotAsync(ISystemContext ctx)
        {
            try
            {
                var tempCount = (await ctx.TempMemories.GetAllAsync()).Count;
                var undreamed = (await ctx.Memories.GetUndreamedAsync(100)).Count;
                stats.UpdateSnapshot(tempCount, undreamed);
            }
            catch { }
        }

        public bool ShouldSpawn(EngineEvent e, ISystemContext ctx)
        {
            // 只在 TickEvent 时评估（避免每个事件都跑）
            if (e is not TimerEvent timer || timer.TimerName != "tick")
            {
                // 但 force-sleep 需要立即响应
                if (forceFlag)
                {
                    forceFlag = false;
                    pendingLevel = SleepLevel.DeepSleep;
                    pendingMaxFragments = cfg.MaxFragmentsPerDeepSleep;
                    return !ctx.HasActiveEngine("Dream");
                }
                return false;
            }

            if (!ctx.IsIdle || ctx.HasActiveEngine("Dream")) return false;

            // 许可超时自动授予
            if (permissionRequestTime != null && !dreamPermission &&
                (DateTime.Now - permissionRequestTime.Value).TotalSeconds > cfg.PermissionTimeout)
            {
                dreamPermission = true;
                FrameworkLogger.Log("DreamSpawnCheck", "许可超时自动授予");
            }

            // ① 已锁定 → 只等许可
            if (permissionRequestTime != null)
            {
                if (dreamPermission)
                {
                    pendingLevel = SleepLevel.DeepSleep;
                    pendingMaxFragments = cfg.MaxFragmentsPerDeepSleep;
                    return true;
                }
                return false;
            }

            // ② 红色评估
            if (EvaluateRed(ctx))
            {
                permissionRequestTime = DateTime.Now;
                FrameworkLogger.Log("DreamSpawnCheck", "红色触发，锁定，等待许可");
                HandleRedStats();
                if (dreamPermission)
                {
                    pendingLevel = SleepLevel.DeepSleep;
                    pendingMaxFragments = cfg.MaxFragmentsPerDeepSleep;
                    return true;
                }
                return false;
            }

            // ③ 黄色评估
            var score = EvaluateYellow(ctx);
            if (score >= cfg.YellowThreshold)
            {
                permissionRequestTime = DateTime.Now;
                FrameworkLogger.Log("DreamSpawnCheck",
                    $"黄色 {score:F2} >= {cfg.YellowThreshold}，锁定，等待许可");
                if (dreamPermission)
                {
                    pendingLevel = SleepLevel.DeepSleep;
                    pendingMaxFragments = cfg.MaxFragmentsPerDeepSleep;
                    return true;
                }
                return false;
            }

            // ④ 小睡
            if (ctx.IdleDuration.TotalSeconds > cfg.NapIdleThreshold)
            {
                pendingLevel = SleepLevel.Nap;
                pendingMaxFragments = cfg.MaxFragmentsPerNap;
                return true;
            }

            // ⑤ 走神
            if (lastDaydreamTime == null ||
                (DateTime.Now - lastDaydreamTime.Value).TotalSeconds > cfg.DaydreamCooldown)
            {
                pendingLevel = SleepLevel.Daydream;
                pendingMaxFragments = 1;
                return true;
            }

            return false;
        }

        public ISubEngine Create(ISystemContext ctx)
        {
            // 大睡开始时消耗许可
            if (pendingLevel == SleepLevel.DeepSleep)
            {
                dreamPermission = false;
                customRedAlert = false;
            }

            return new DreamEngine(ctx, pendingLevel, pendingMaxFragments, this);
        }

        // ---- 供 DreamEngine 实例访问 ----

        internal DreamConfig GetConfig() => cfg;

        // ---- 供 DreamEngine 实例回调 ----

        internal void OnDreamCompleted(SleepLevel level, int processed)
        {
            if (level == SleepLevel.Daydream)
                lastDaydreamTime = DateTime.Now;
            else if (level == SleepLevel.DeepSleep)
            {
                lastDeepSleepTime = DateTime.Now;
                stats.RecordProcessed(processed);
                stats.PruneAndRecalcBaseline();
                stats.ResetRedDays();
                stats.Save(DreamStatsPath);
                permissionRequestTime = null;
                scoreOffset = 0f;
                FrameworkLogger.Log("DreamSpawnCheck", "大睡完成，统计更新，状态重置");
            }
        }

        // ---- 红/黄评估 ----

        private bool EvaluateRed(ISystemContext ctx)
        {
            if (customRedAlert) return true;

            var tempCount = ctx.TempMemories.GetAllAsync().Result.Count;
            var baseline = stats.GetBaselineAvg();
            if (tempCount > cfg.RedTempMultiplier * baseline) return true;

            if (lastDeepSleepTime != null &&
                (DateTime.Now - lastDeepSleepTime.Value).TotalHours > cfg.RedMaxSleepGapHours)
                return true;

            return false;
        }

        private float EvaluateYellow(ISystemContext ctx)
        {
            float total = cfg.ScoreBase + scoreOffset;
            total += LinearScore((float)ctx.IdleDuration.TotalSeconds,
                600f, cfg.DeepSleepIdleThreshold, 3f);

            if (lastDeepSleepTime != null)
                total += LinearScore((float)(DateTime.Now - lastDeepSleepTime.Value).TotalHours,
                    12f, 48f, 3f);

            var tempCount = ctx.TempMemories.GetAllAsync().Result.Count;
            var baseline = stats.GetBaselineAvg();
            if (baseline > 0)
                total += LinearScore(tempCount / baseline, 1f, 3f, 3f);

            var undreamed = ctx.Memories.GetUndreamedAsync(100).Result.Count;
            total += LinearScore(undreamed, 0f, 40f, 1f);

            total += cfg.CalcTimeWindowScore(3f);

            FrameworkLogger.Log("DreamSpawnCheck",
                $"黄色评分: {total:F2} (base={cfg.ScoreBase}, offset={scoreOffset})");
            return total;
        }

        private void HandleRedStats()
        {
            stats.IncrementRedDays();
            if (stats.ConsecutiveRedDays >= 3)
            {
                stats.Baseline.AvgDailyTempIntake *= 1.2f;
                FrameworkLogger.Log("DreamSpawnCheck",
                    $"连续 {stats.ConsecutiveRedDays} 天红色，基线上调至 {stats.Baseline.AvgDailyTempIntake:F1}");
                stats.ResetRedDays();
            }
            stats.Save(DreamStatsPath);
        }

        private static float LinearScore(float value, float min, float max, float maxScore)
        {
            if (max <= min) return 0f;
            return Math.Clamp((value - min) / (max - min), 0f, 1f) * maxScore;
        }
    }
}
