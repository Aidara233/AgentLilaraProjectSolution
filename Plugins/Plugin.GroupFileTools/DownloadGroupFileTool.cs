using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.GroupFileTools;

internal static class GroupFileToolArgs
{
    /// <summary>
    /// 生成安全文件名：去除路径非法字符；Windows 下再收敛为 ASCII 字母数字，防止中文名
    /// 因控制台/编码问题落盘成 "???"。结果为空时回退 fallback。
    /// </summary>
    public static string MakeSafeName(string? name, string fallback)
    {
        var raw = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        var safe = string.Join("_", raw.Split(Path.GetInvalidFileNameChars())).Trim();
        if (OperatingSystem.IsWindows())
        {
            var ascii = new string(safe.Select(c => c < 128 ? c : '_').ToArray());
            safe = string.Join("_", ascii.Split(Path.GetInvalidFileNameChars())).Trim(' ', '.');
        }
        return string.IsNullOrEmpty(safe) || safe.All(c => c == '_') ? fallback : safe;
    }
}

[ToolMeta(Group = null, ContinueLoop = true, OutputOnly = true)]
public class DownloadGroupFileTool : ITool
{
    private readonly IAdapterAccess? _adapterAccess;
    private readonly IChannelAccess? _channelAccess;
    private readonly IConnectedChannel? _connected;
    private readonly IWorkspaceAccess? _workspace;
    private readonly HttpClient _http;
    private readonly GroupFileDownloadStore? _store;
    private readonly string _sessionId = "";

    public DownloadGroupFileTool() { _http = new HttpClient(); }

    public DownloadGroupFileTool(IAdapterAccess adapterAccess, IChannelAccess channelAccess,
        IConnectedChannel? connected, IWorkspaceAccess workspace, HttpClient http,
        GroupFileDownloadStore store, string sessionId)
    {
        _adapterAccess = adapterAccess;
        _channelAccess = channelAccess;
        _connected = connected;
        _workspace = workspace;
        _http = http;
        _store = store;
        _sessionId = sessionId;
    }

    public string Name => "download_group_file";
    public string Description => "下载群文件到本地（群聊私聊均可用）。"
        + "先从 list_group_files 获取 file_id 和 busid，然后调用此工具直接下载。"
        + "下载在后台执行，完成后自动通知。"
        + "参数: file_id(文件ID), busid(业务ID，默认102), file_name(期望的文件名，可选)，"
        + "fallback_group_id(群号，可选；群文件列表里只有 file_id 没有群号时的兜底)。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("file_id", "文件ID（从 list_group_files 获取）", 0),
        new("busid", "业务ID（默认102）", 1, isRequired: false),
        new("file_name", "期望的文件名（可选，用于完成通知；务必传真实文件名，避免落盘成 file_id）", 2, isRequired: false),
        new("fallback_group_id", "兜底群号（可选，仅在当前频道的 group_id 不可用时生效）", 3, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (_adapterAccess == null)
            return new ToolResult { Status = "failed", Error = "适配器服务不可用" };

        var fileId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var busid = resolvedInputs.Count > 1 ? resolvedInputs[1]?.Trim() ?? "102" : "102";
        var fileName = resolvedInputs.Count > 2 ? resolvedInputs[2]?.Trim() : null;
        var fallbackGroupId = resolvedInputs.Count > 3 ? resolvedInputs[3]?.Trim() : null;
        if (string.IsNullOrWhiteSpace(busid)) busid = "102";

        if (string.IsNullOrEmpty(fileId))
            return new ToolResult { Status = "failed", Error = "file_id 不能为空" };

        // 运行期解析频道（组件装配早于 AutoClaim，不能在 OnInit 里取 CurrentChannelId）：
        // 群号取当前频道且必须是群，否则用 fallback_group_id
        var groupId = "";
        var channelId = _connected?.CurrentChannelId ?? 0;
        if (channelId > 0 && _channelAccess != null)
        {
            var name = (await _channelAccess.GetChannelDetailAsync(channelId))?.Name;
            if (!string.IsNullOrEmpty(name) && name.StartsWith("group_", StringComparison.OrdinalIgnoreCase))
                groupId = name[6..];
        }
        if (string.IsNullOrEmpty(groupId))
            groupId = fallbackGroupId ?? "";
        if (string.IsNullOrEmpty(groupId))
            return new ToolResult
            {
                Status = "failed",
                Error = "无法确定群号：当前频道不是群，请补 fallback_group_id"
            };

        var adapterId = _adapterAccess.GetAdapterIdForChannel($"group_{groupId}");
        if (adapterId == null)
            return new ToolResult { Status = "failed", Error = "未找到可处理 QQ 群的适配器" };

        // 获取下载 URL
        var url = await _adapterAccess.ExecuteActionAsync(adapterId, "get_group_file_url",
            $"{{\"group_id\":\"{groupId}\",\"file_id\":\"{fileId}\",\"busid\":{busid}}}");
        if (string.IsNullOrEmpty(url))
            return new ToolResult { Status = "failed", Error = "获取下载链接失败，请确认 file_id 和 busid 正确" };

        var saveName = fileName ?? fileId;
        var safeName = GroupFileToolArgs.MakeSafeName(fileName, fileId);
        var destDir = _workspace?.Resolve("Downloads");
        if (destDir == null)
            return new ToolResult { Status = "failed", Error = "工作区不可用" };
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, safeName);

        // 注册下载任务
        var store = _store;
        var task = store?.Register(_sessionId, saveName, destPath);
        var taskId = task?.Id ?? "";

        var http = _http;
        var sessionId = _sessionId;
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
                    if (task != null) task.SavePath = destPath;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync();
                await using var fileStream = new FileStream(destPath, FileMode.Create,
                    FileAccess.Write, FileShare.None, 8192, useAsync: true);
                await stream.CopyToAsync(fileStream);

                var size = new FileInfo(destPath).Length;
                store?.MarkCompleted(taskId, size);
            }
            catch (Exception ex)
            {
                store?.MarkFailed(taskId, ex.Message);
            }
        }, CancellationToken.None);

        return new ToolResult { Status = "success", Data = $"[下载已提交] {saveName}" };
    }
}
