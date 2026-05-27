// Plugins/Plugin.NetworkTools/CancelDownloadTool.cs
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "network", ContinueLoop = true)]
public class CancelDownloadTool : ITool
{
    private readonly DownloadStore _store;

    public string Name => "cancel_download";
    public string Description => "取消指定下载任务，删除已写入的部分文件。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("download_id", "下载ID（由download_file返回）", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public CancelDownloadTool(DownloadStore store)
    {
        _store = store;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var id = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";

        if (string.IsNullOrEmpty(id))
            return Fail("download_id不能为空");

        var task = _store.Get(id);
        if (task == null)
            return Fail($"未找到下载任务: {id}（可能已完成或不存在）");

        if (task.Status == DownloadStatus.Completed)
            return Fail($"下载已完成，无法取消: {id}");

        var cancelled = _store.Cancel(id);
        return cancelled
            ? Ok($"已取消下载: {task.FileName}")
            : Fail($"取消失败: {id}");
    }

    private static Task<ToolResult> Fail(string err) =>
        Task.FromResult(new ToolResult { Status = "failed", Error = err });
    private static Task<ToolResult> Ok(string data) =>
        Task.FromResult(new ToolResult { Status = "success", Data = data });
}
