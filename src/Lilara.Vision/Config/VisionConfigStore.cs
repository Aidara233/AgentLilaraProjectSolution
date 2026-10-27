using Newtonsoft.Json;

namespace AgentCoreProcessor.Engine.Vision;

/// <summary>显式文件路径的视觉配置存储；不读取宿主全局配置。</summary>
internal sealed class VisionConfigStore<T>(string path) where T : VisionEngineConfigData, new()
{
    public T Load()
    {
        if (!File.Exists(path))
        {
            var config = new T();
            Save(config);
            return config;
        }
        try { return JsonConvert.DeserializeObject<T>(File.ReadAllText(path)) ?? new T(); }
        catch { return new T(); }
    }

    public void Save(T config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
    }
}
