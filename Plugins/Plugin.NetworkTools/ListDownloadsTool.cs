// Plugins/Plugin.NetworkTools/ListDownloadsTool.cs
using System.Text;
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "network", ContinueLoop = true)]
public class ListDownloadsTool : ITool
{
    private readonly DownloadStore _store;
    private readonly string _loopId;

    public string Name => "list_downloads";
    public string Description => "查看下载任务列表。可按状态筛选，默认仅显示本循环发起的下载。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("filter", "筛选: all(默认)/active/completed/failed（可选）", 0, isRequired: false),
        new("loop_only", "仅本循环的下载，默认true（可选）", 1, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public ListDownloadsTool(DownloadStore store, string loopId)
    {
        _store = store;
        _loopId = loopId;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var filter = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim().ToLower() : "all";
        var loopOnlyStr = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToLower() : "true";
        var loopOnly = loopOnlyStr != "false";

        if (filter is not ("all" or "active" or "completed" or "failed"))
            filter = "all";

        var tasks = _store.GetAll(filter, loopOnly ? _loopId : null);

        if (tasks.Count == 0)
            return Ok("没有符合条件的下载任务");

        var sb = new StringBuilder();
        sb.AppendLine($"[下载任务] 共{ tasks.Count }个");

        foreach (var t in tasks)
        {
            var statusIcon = t.Status switch
            {
                DownloadStatus.Downloading => "↓",
                DownloadStatus.Completed => "✓",
                DownloadStatus.Failed => "✗",
                DownloadStatus.Cancelled => "⊘",
                _ => "·"
            };
            var progress = t.ProgressPct >= 0 ? $" {t.ProgressPct}%" : "";
            var speed = t.SpeedString != null ? $" {t.SpeedString}" : "";
            var size = t.TotalBytes.HasValue ? t.SizeString : "?";

            sb.Append($"  {statusIcon} [{t.Id}] {t.FileName}");
            if (t.Status == DownloadStatus.Downloading)
                sb.Append($" ({size}{progress}{speed})");
            else if (t.Status == DownloadStatus.Completed)
                sb.Append($" ({size}) -> {t.RelativePath}");
            else if (t.Status == DownloadStatus.Failed)
                sb.Append($" ({t.Error})");

            sb.AppendLine();
        }

        return Ok(sb.ToString().TrimEnd());
    }

    private static Task<ToolResult> Ok(string data) =>
        Task.FromResult(new ToolResult { Status = "success", Data = data });
}
