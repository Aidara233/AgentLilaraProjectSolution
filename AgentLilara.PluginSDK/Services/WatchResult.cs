namespace AgentLilara.PluginSDK.Services;

/// <summary>订阅操作的结果。</summary>
public sealed class WatchResult
{
    public bool Success { get; set; }
    public string? WatchId { get; set; }
    public string? Error { get; set; }

    public static WatchResult Ok(string watchId) => new() { Success = true, WatchId = watchId };

    public static WatchResult Fail(string error) => new() { Success = false, Error = error };
}
