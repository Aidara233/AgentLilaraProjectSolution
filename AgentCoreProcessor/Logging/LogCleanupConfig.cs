using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Logging;

internal class LogCleanupConfig
{
    public int SignalLogMaxMB { get; set; } = 300;
    public int ModelLogMaxMB { get; set; } = 500;
    public int CheckIntervalMinutes { get; set; } = 60;
    public int TokenUsageRetainDays { get; set; } = 30;

    public static LogCleanupConfig Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<LogCleanupConfig>(json) ?? new LogCleanupConfig();
            }
            catch { }
        }
        var cfg = new LogCleanupConfig();
        Save(cfg, path);
        return cfg;
    }

    public static void Save(LogCleanupConfig cfg, string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonConvert.SerializeObject(cfg, Formatting.Indented));
        }
        catch { }
    }
}
