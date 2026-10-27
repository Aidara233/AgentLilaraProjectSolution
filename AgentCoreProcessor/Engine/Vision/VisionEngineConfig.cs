namespace AgentCoreProcessor.Engine.Vision;

/// <summary>宿主兼容入口；配置字段与文件读写由视觉库提供。</summary>
internal class VisionEngineConfig : VisionEngineConfigData
{
    private static VisionConfigStore<VisionEngineConfig> Store => new(Path.Combine(
        Config.PathConfig.StoragePath, "Engine", "VisionEngineConfig.json"));

    public static VisionEngineConfig Load() => Store.Load();
    public void Save() => Store.Save(this);
}
