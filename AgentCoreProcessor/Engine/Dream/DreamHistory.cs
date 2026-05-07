using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AgentCoreProcessor.Config;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine
{
    internal class DreamHistoryEntry
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public string Level { get; set; } = "";
        public int FragmentsExecuted { get; set; }
        public bool WasInterrupted { get; set; }
    }

    internal static class DreamHistory
    {
        private static string FilePath =>
            Path.Combine(PathConfig.StoragePath, "Dream", "DreamHistory.json");

        public static void Append(DreamHistoryEntry entry)
        {
            try
            {
                var entries = Load();
                entries.Add(entry);

                // 保留最近 100 条
                if (entries.Count > 100)
                    entries = entries.Skip(entries.Count - 100).ToList();

                var dir = Path.GetDirectoryName(FilePath);
                if (dir != null) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(entries, Formatting.Indented));
            }
            catch (Exception ex)
            {
                FrameworkLogger.Log("DreamHistory", $"写入失败: {ex.Message}");
            }
        }

        public static List<DreamHistoryEntry> Load()
        {
            if (!File.Exists(FilePath)) return new();

            try
            {
                var json = File.ReadAllText(FilePath);
                return JsonConvert.DeserializeObject<List<DreamHistoryEntry>>(json) ?? new();
            }
            catch
            {
                return new();
            }
        }
    }
}
