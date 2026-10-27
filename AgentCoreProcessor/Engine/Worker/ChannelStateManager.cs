using System.Collections.Concurrent;
using AgentCoreProcessor.Config;

namespace AgentCoreProcessor.Engine;

/// <summary>兼容宿主现有调用；文件存储实现由频道模块提供。</summary>
internal static class ChannelStateManager
{
    private static readonly ConcurrentDictionary<string, IChannelStore> Stores =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static IChannelStore Store
    {
        get
        {
            var path = Path.GetFullPath(string.IsNullOrEmpty(PathConfig.StoragePath) ? "." : PathConfig.StoragePath);
            return Stores.GetOrAdd(path, static root => new FileChannelStore(root));
        }
    }

    public static ChannelConfig LoadConfig(int channelId, float? dbAffinity = null) =>
        Store.LoadConfig(channelId, dbAffinity);

    public static void SaveConfig(int channelId, ChannelConfig config) => Store.SaveConfig(channelId, config);
}
