using System;
using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 做梦调度配置。支持运行时修改和持久化。
    /// </summary>
    internal class DreamConfig
    {
        /// <summary>走神冷却期（秒）</summary>
        public int DaydreamCooldown { get; set; } = 120;

        /// <summary>小睡空闲阈值（秒）</summary>
        public int NapIdleThreshold { get; set; } = 600;

        /// <summary>小睡冷却期（秒），完成一次小睡后多久才能再次触发</summary>
        public int NapCooldown { get; set; } = 600;

        /// <summary>大睡空闲阈值（秒）</summary>
        public int DeepSleepIdleThreshold { get; set; } = 1800;

        /// <summary>大睡时间段开始（HH:mm）</summary>
        public string DeepSleepTimeStart { get; set; } = "02:00";

        /// <summary>大睡时间段结束（HH:mm）</summary>
        public string DeepSleepTimeEnd { get; set; } = "06:00";

        /// <summary>大睡时间段峰值（HH:mm），非对称评分的最高点</summary>
        public string DeepSleepTimePeak { get; set; } = "00:00";

        /// <summary>管理员许可超时自动许可（秒）</summary>
        public int PermissionTimeout { get; set; } = 3600;

        /// <summary>调度循环检查间隔（秒）</summary>
        public int ScheduleInterval { get; set; } = 30;

        /// <summary>小睡最大片段数</summary>
        public int MaxFragmentsPerNap { get; set; } = 12;

        /// <summary>大睡最大片段数</summary>
        public int MaxFragmentsPerDeepSleep { get; set; } = 120;

        // ---- 大睡分级配置 ----

        /// <summary>黄色评分阈值，总分达到此值触发许可请求</summary>
        public float YellowThreshold { get; set; } = 5.0f;

        /// <summary>持久评分偏置（Agent 体质：正=嗜睡，负=夜猫子）</summary>
        public float ScoreBase { get; set; } = 0.0f;

        /// <summary>红色条件：临时记忆超过基线均值的倍率</summary>
        public float RedTempMultiplier { get; set; } = 3.0f;

        /// <summary>红色条件：距上次大睡最大小时数</summary>
        public float RedMaxSleepGapHours { get; set; } = 48.0f;

        // ---- 预算配置 ----

        /// <summary>大睡 DreamEngine 片段调用的 token 预算</summary>
        public int DeepSleepTokenBudget { get; set; } = 100000;

        /// <summary>大睡硬性时间上限（分钟）</summary>
        public int DeepSleepMaxMinutes { get; set; } = 120;

        // ---- 整合配置 ----

        /// <summary>整合第一轮每批最大条数</summary>
        public int ConsolidationBatchSize { get; set; } = 50;

        /// <summary>小组合并阈值：subject 条数低于此值时归入杂项批</summary>
        public int ConsolidationSmallGroupThreshold { get; set; } = 5;

        // ---- 片段参数配置 ----

        /// <summary>权重评估每次处理的记忆条数</summary>
        public int WeightBatchSize { get; set; } = 10;

        /// <summary>关联重建每次处理的目标记忆数</summary>
        public int LinkTargetCount { get; set; } = 3;

        /// <summary>关联重建候选池大小（embedding 搜索返回数）</summary>
        public int LinkCandidatePoolSize { get; set; } = 20;

        /// <summary>关联重建 cosine 相似度最低阈值</summary>
        public float LinkCosineThreshold { get; set; } = 0.3f;

        /// <summary>关联重建过滤后取 top-k 候选</summary>
        public int LinkTopK { get; set; } = 10;

        /// <summary>记忆组合搜索的近期记忆池大小</summary>
        public int CombineRecentPoolSize { get; set; } = 30;

        /// <summary>记忆组合要求的最低关联强度</summary>
        public float CombineStrengthThreshold { get; set; } = 0.7f;

        /// <summary>记忆组合每次尝试的最大对数</summary>
        public int CombineMaxPairs { get; set; } = 3;

        // ---- 去重配置 ----

        /// <summary>去重集群最大条数（种子+关联）</summary>
        public int DedupClusterSize { get; set; } = 12;

        /// <summary>去重集群最小条数，低于此数不触发</summary>
        public int DedupMinClusterSize { get; set; } = 3;

        /// <summary>判断当前时间是否在大睡时间段内。</summary>
        public bool IsInDeepSleepWindow()
        {
            if (!TimeSpan.TryParse(DeepSleepTimeStart, out var start) ||
                !TimeSpan.TryParse(DeepSleepTimeEnd, out var end))
                return false;

            var now = DateTime.Now.TimeOfDay;
            // 支持跨午夜（如 23:00 - 06:00）
            return start < end
                ? now >= start && now <= end
                : now >= start || now <= end;
        }

        /// <summary>
        /// 计算当前时间在大睡窗口中的非对称评分。
        /// 窗口外返回 0，从 Start 到 Peak 线性上升至 maxScore，从 Peak 到 End 线性下降至 0。
        /// </summary>
        public float CalcTimeWindowScore(float maxScore)
        {
            if (!TimeSpan.TryParse(DeepSleepTimeStart, out var start) ||
                !TimeSpan.TryParse(DeepSleepTimeEnd, out var end) ||
                !TimeSpan.TryParse(DeepSleepTimePeak, out var peak))
                return 0f;

            var now = DateTime.Now.TimeOfDay;

            // 归一化到以 start 为零点的线性空间，消除跨午夜问题
            var totalWindow = Normalize(end, start);
            var peakNorm = Normalize(peak, start);
            var nowNorm = Normalize(now, start);

            if (nowNorm > totalWindow)
                return 0f; // 窗口外

            if (peakNorm <= TimeSpan.Zero)
                peakNorm = totalWindow; // peak == start 时整个窗口都是下降段

            if (nowNorm <= peakNorm)
            {
                // 上升段
                return peakNorm > TimeSpan.Zero
                    ? (float)(nowNorm / peakNorm) * maxScore
                    : maxScore;
            }
            else
            {
                // 下降段
                var falling = totalWindow - peakNorm;
                return falling > TimeSpan.Zero
                    ? (float)((totalWindow - nowNorm) / falling) * maxScore
                    : 0f;
            }
        }

        /// <summary>将时间归一化到以 origin 为零点的 24h 空间。</summary>
        private static TimeSpan Normalize(TimeSpan time, TimeSpan origin)
        {
            var diff = time - origin;
            if (diff < TimeSpan.Zero) diff += TimeSpan.FromHours(24);
            return diff;
        }

        /// <summary>从 JSON 文件加载配置。文件不存在时返回默认配置。</summary>
        public static DreamConfig Load(string path)
        {
            if (!File.Exists(path))
                return new DreamConfig();

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<DreamConfig>(json) ?? new DreamConfig();
        }

        /// <summary>保存配置到 JSON 文件。</summary>
        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
