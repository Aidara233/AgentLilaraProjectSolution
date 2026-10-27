namespace AgentCoreProcessor.Adapter;

/// <summary>平台解析器的媒体归档端口，由宿主提供存储实现。</summary>
public interface IAdapterMediaStore
{
    Task<(string LocalPath, string Hash)> DownloadAndSaveAsync(string url, HttpClient client);
    Task SetCategoryAsync(string hash, string category);
}
