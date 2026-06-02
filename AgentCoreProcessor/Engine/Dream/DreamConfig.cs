using System;
using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 梦境配置：秩序 + 巡逻 + 并行度。
    /// </summary>
    internal class DreamConfig
    {
        // ---- 并行 ----

        public int EmbedParallelLimit { get; set; } = 4;
        public int MaxPatrolSteps { get; set; } = 100;

        // ---- 传播 ----

        public float ChangePropagationEpsilon { get; set; } = 0.01f;

        // ---- 秩序阶段 ----

        public float OrderClassifyMinCos { get; set; } = 0.7f;
        public float OrderMergeMinSupport { get; set; } = 0.9f;

        // ---- 巡逻 ----

        public int ColdStartPoolSize { get; set; } = 20;
        public float TriangleClassifyMinCos { get; set; } = 0.7f;
        public int TriangleBufferSize { get; set; } = 10;
        public int RelationBatchMaxTargets { get; set; } = 8;
        public float DecayThreshold { get; set; } = 0.05f;

        // ---- Review 触发 ----

        /// <summary>距上次 Review 完成的最小间隔（小时）。0 表示禁用自动触发。</summary>
        public int ReviewIntervalHours { get; set; } = 4;

        // ---- IO ----

        public static DreamConfig Load(string path)
        {
            if (!File.Exists(path))
                return new DreamConfig();

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<DreamConfig>(json) ?? new DreamConfig();
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
