using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    internal class DailyRecord
    {
        public string Date { get; set; } = "";
        public int TempPeak { get; set; }
        public int Processed { get; set; }
        public int UndreamedPeak { get; set; }
    }

    internal class DreamBaseline
    {
        public float AvgDailyTempIntake { get; set; } = 50f;
        public string LastUpdate { get; set; } = "";
    }

    /// <summary>
    /// 做梦统计数据。滚动 7 天窗口，用于自适应基线计算。
    /// </summary>
    internal class DreamStats
    {
        private const int MaxDays = 7;
        private const float DefaultBaseline = 50f;

        public List<DailyRecord> DailyRecords { get; set; } = new();
        public DreamBaseline Baseline { get; set; } = new();
        public int ConsecutiveRedDays { get; set; } = 0;

        /// <summary>获取基线均值。无数据时返回默认值。</summary>
        public float GetBaselineAvg()
        {
            return Baseline.AvgDailyTempIntake > 0
                ? Baseline.AvgDailyTempIntake
                : DefaultBaseline;
        }

        /// <summary>确保今天的记录存在，返回该记录。</summary>
        public DailyRecord EnsureToday()
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var record = DailyRecords.FirstOrDefault(r => r.Date == today);
            if (record == null)
            {
                record = new DailyRecord { Date = today };
                DailyRecords.Add(record);
            }
            return record;
        }

        /// <summary>更新当天快照（取峰值）。</summary>
        public void UpdateSnapshot(int tempCount, int undreamedCount)
        {
            var record = EnsureToday();
            if (tempCount > record.TempPeak) record.TempPeak = tempCount;
            if (undreamedCount > record.UndreamedPeak) record.UndreamedPeak = undreamedCount;
        }

        /// <summary>记录大睡处理量。</summary>
        public void RecordProcessed(int count)
        {
            var record = EnsureToday();
            record.Processed += count;
        }

        /// <summary>修剪超过 7 天的记录并重算基线。</summary>
        public void PruneAndRecalcBaseline()
        {
            var cutoff = DateTime.Now.AddDays(-MaxDays).ToString("yyyy-MM-dd");
            DailyRecords.RemoveAll(r => string.Compare(r.Date, cutoff, StringComparison.Ordinal) < 0);

            if (DailyRecords.Count > 0)
                Baseline.AvgDailyTempIntake = (float)DailyRecords.Average(r => r.TempPeak);
            else
                Baseline.AvgDailyTempIntake = DefaultBaseline;

            Baseline.LastUpdate = DateTime.Now.ToString("yyyy-MM-dd");
        }

        public void IncrementRedDays() => ConsecutiveRedDays++;
        public void ResetRedDays() => ConsecutiveRedDays = 0;

        /// <summary>从 JSON 文件加载。文件不存在时返回默认。</summary>
        public static DreamStats Load(string path)
        {
            if (!File.Exists(path))
                return new DreamStats();

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<DreamStats>(json) ?? new DreamStats();
        }

        /// <summary>保存到 JSON 文件。</summary>
        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }
}
