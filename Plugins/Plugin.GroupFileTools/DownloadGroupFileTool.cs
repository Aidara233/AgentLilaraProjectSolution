using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.GroupFileTools;

[ToolMeta(Group = null, ContinueLoop = true, ExpressAvailable = true, OutputOnly = true)]
public class DownloadGroupFileTool : ITool
{
    private readonly IAdapterAccess? _adapterAccess;
    private readonly IChannelAccess? _channelAccess;
    private readonly string _adapterId = "";
    private readonly int _channelId;
    private readonly string _workspaceDir = "";
    private readonly HttpClient _http;

    public DownloadGroupFileTool() { _http = new HttpClient(); }

    public DownloadGroupFileTool(IAdapterAccess adapterAccess, IChannelAccess channelAccess,
        string adapterId, int channelId, string workspaceDir, HttpClient http)
    {
        _adapterAccess = adapterAccess;
        _channelAccess = channelAccess;
        _adapterId = adapterId;
        _channelId = channelId;
        _workspaceDir = workspaceDir;
        _http = http;
    }

    public string Name => "download_group_file";
    public string Description => "一步下载群文件到本地。先从 list_group_files 获取 file_id 和 busid，"
        + "然后调用此工具直接下载。参数: group_id(群号), file_id(文件ID), busid(业务ID，默认102), "
        + "file_name(期望的文件名，可选)。文件较大时会后台下载并完成后通知。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("group_id", "群号", 0),
        new("file_id", "文件ID（从 list_group_files 获取）", 1),
        new("busid", "业务ID（默认102）", 2, isRequired: false),
        new("file_name", "期望的文件名（可选，用于完成通知）", 3, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (_adapterAccess == null)
            return new ToolResult { Status = "failed", Error = "适配器服务不可用" };

        var groupId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var fileId = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
        var busid = resolvedInputs.Count > 2 ? resolvedInputs[2]?.Trim() ?? "102" : "102";
        var fileName = resolvedInputs.Count > 3 ? resolvedInputs[3]?.Trim() : null;

        if (string.IsNullOrEmpty(groupId) || string.IsNullOrEmpty(fileId))
            return new ToolResult { Status = "failed", Error = "group_id 和 file_id 不能为空" };

        // 获取下载 URL
        var url = await _adapterAccess.ExecuteActionAsync(_adapterId, "get_group_file_url",
            $"{{\"group_id\":\"{groupId}\",\"file_id\":\"{fileId}\",\"busid\":{busid}}}");
        if (string.IsNullOrEmpty(url))
            return new ToolResult { Status = "failed", Error = "获取下载链接失败，请确认 file_id 和 busid 正确" };

        // 后台下载
        var saveName = fileName ?? $"{fileId}";
        var safeName = string.Join("_", saveName.Split(Path.GetInvalidFileNameChars()));
        var destDir = Path.Combine(_workspaceDir, "Downloads");
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, safeName);

        var http = _http;
        var channelAccess = _channelAccess;
        var channelId = _channelId;
        _ = Task.Run(async () =>
        {
            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();

                var cd = resp.Content.Headers.ContentDisposition;
                if (cd?.FileName != null)
                {
                    var realName = cd.FileName.Trim('"');
                    var realSafe = string.Join("_", realName.Split(Path.GetInvalidFileNameChars()));
                    destPath = Path.Combine(destDir, realSafe);
                }

                await using var stream = await resp.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(destPath, FileMode.Create,
                    FileAccess.Write, FileShare.None, 8192, useAsync: true);
                await stream.CopyToAsync(fileStream);
            }
            catch (Exception ex)
            {
                if (channelAccess != null)
                    await channelAccess.SendMessageAsync(channelId, $"[下载失败] {saveName}: {ex.Message}");
                return;
            }

            if (channelAccess != null)
            {
                var resultName = Path.GetFileName(destPath);
                var sizeStr = "";
                try
                {
                    var fi = new FileInfo(destPath);
                    if (fi.Exists && fi.Length >= 1_000_000)
                        sizeStr = $" ({fi.Length / 1_000_000.0:F1}MB)";
                    else if (fi.Exists)
                        sizeStr = $" ({fi.Length / 1_000.0:F1}KB)";
                }
                catch { }
                await channelAccess.SendMessageAsync(channelId, $"[下载完成] {resultName}{sizeStr}");
            }
        }, CancellationToken.None);

        return new ToolResult { Status = "success", Data = $"[下载已提交] {saveName}" };
    }
}
