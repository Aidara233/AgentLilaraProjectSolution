using System;
using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    /// <summary>
    /// 梦境配置：巡逻步数 + 秩序参数 + 预算。
    /// </summary>
    internal class DreamConfig
    {
        // ---- 巡逻步数 ----

        public int DaydreamPatrolSteps { get; set; } = 3;
        public int NapPatrolSteps { get; set; } = 15;
        public int MaxPatrolSteps { get; set; } = 100;

        // ---- 预算 ----

        public int MainTokenBudget { get; set; } = 100000;
        public int ReserveTokenBudget { get; set; } = 30000;
        public int DeepSleepMaxMinutes { get; set; } = 120;

        // ---- 传播 ----

        public float ChangePropagationEpsilon { get; set; } = 0.01f;

        // ---- 秩序阶段 ----

        public int EmbeddingTopK { get; set; } = 10;
        public float OrderClassifyMinCos { get; set; } = 0.7f;
        public float OrderMergeMinSupport { get; set; } = 0.9f;

        // ---- 巡逻 ----

        public int ColdStartPoolSize { get; set; } = 20;
        public float TriangleClassifyMinCos { get; set; } = 0.7f;
        public int TriangleBufferSize { get; set; } = 10;
        public int RelationBatchMaxTargets { get; set; } = 8;
        public float DecayThreshold { get; set; } = 0.05f;

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
