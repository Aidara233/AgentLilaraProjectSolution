using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine;

internal interface IChannelStore
{
    ChannelConfig LoadConfig(int channelId, float? dbAffinity = null);
    void SaveConfig(int channelId, ChannelConfig config);
}

/// <summary>频道配置文件后端；路径由宿主显式提供，JSON 存在时保持其优先级。</summary>
internal sealed class FileChannelStore : IChannelStore
{
    private readonly string _directory;
    private readonly object _gate = new();

    public FileChannelStore(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        _directory = Path.Combine(Path.GetFullPath(storagePath), "ChannelState");
    }

    public ChannelConfig LoadConfig(int channelId, float? dbAffinity = null)
    {
        lock (_gate)
        {
            var path = GetPath(channelId);
            if (File.Exists(path))
                return JsonConvert.DeserializeObject<ChannelConfig>(File.ReadAllText(path)) ?? new();
            var config = new ChannelConfig();
            if (dbAffinity.HasValue) config.Affinity = dbAffinity.Value;
            return config;
        }
    }

    public void SaveConfig(int channelId, ChannelConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            var path = GetPath(channelId);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonConvert.SerializeObject(config, Formatting.Indented));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private string GetPath(int channelId)
    {
        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, $"{channelId}.json");
    }
}
