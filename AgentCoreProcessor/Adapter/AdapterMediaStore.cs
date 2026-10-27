namespace AgentCoreProcessor.Adapter;

internal sealed class AdapterMediaStore : IAdapterMediaStore
{
    public Task<(string LocalPath, string Hash)> DownloadAndSaveAsync(string url, HttpClient client) =>
        ImageStorage.DownloadAndSaveAsync(url, client);
    public Task SetCategoryAsync(string hash, string category) => ImageStorage.SetCategoryAsync(hash, category);
}
