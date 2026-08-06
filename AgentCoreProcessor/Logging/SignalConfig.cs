using System.IO;
using Newtonsoft.Json;

namespace AgentCoreProcessor.Logging;

/// <summary>
/// 信号日志配置（Storage/Logs/signalconfig.json）。
/// MinLevel：Debug=0 / Info=1 / Warn=2 / Error=3，低于该级别的事件不落库。
/// 默认 Info（Debug 事件量大，按需调 0 开启排查）。
/// </summary>
internal class SignalConfig
{
    public int MinLevel { get; set; } = LogLevel.Info;

    public static SignalConfig Load(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<SignalConfig>(json) ?? new SignalConfig();
            }
            catch { }
        }
        var cfg = new SignalConfig();
        Save(cfg, path);
        return cfg;
    }

    public static void Save(SignalConfig cfg, string path)
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
