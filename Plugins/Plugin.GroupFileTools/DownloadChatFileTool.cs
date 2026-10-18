using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.GroupFileTools;

[ToolMeta(Group = null, ContinueLoop = true, OutputOnly = true)]
public class DownloadChatFileTool : ITool
{
    private readonly IAdapterAccess? _adapterAccess;
    private readonly IConnectedChannel? _connected;
    private readonly IWorkspaceAccess? _workspace;
    private readonly GroupFileDownloadStore? _store;
    private readonly string _sessionId = "";

    public DownloadChatFileTool() { }

    public DownloadChatFileTool(IAdapterAccess adapterAccess, IConnectedChannel? connected,
        IWorkspaceAccess workspace, GroupFileDownloadStore store, string sessionId)
    {
        _adapterAccess = adapterAccess;
        _connected = connected;
        _workspace = workspace;
        _store = store;
        _sessionId = sessionId;
    }

    public string Name => "download_chat_file";
    public string Description => "下载私聊/群聊中直接发送的文件。"
        + "先从消息里获取 file_id，然后调用此工具下载。"
        + "下载在后台执行，完成后自动通知。参数: file_id(必填), file_name(可选)。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("file_id", "文件ID（从消息附件的file_id获取）", 0),
        new("file_name", "期望的文件名（可选）", 1, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (_adapterAccess == null)
            return Task.FromResult(new ToolResult { Status = "failed", Error = "适配器服务不可用" });

        var fileId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var fileName = resolvedInputs.Count > 1 ? resolvedInputs[1]?.Trim() : null;

        if (string.IsNullOrEmpty(fileId))
            return Task.FromResult(new ToolResult { Status = "failed", Error = "file_id 不能为空" });

        // 运行期解析适配器（组件装配早于 AutoClaim，不能在 OnInit 里锁定 adapterId）
        var channelId = _connected?.CurrentChannelId ?? 0;
        if (channelId <= 0)
            return Task.FromResult(new ToolResult { Status = "failed", Error = "当前会话未连接频道" });
        var adapterId = _adapterAccess.GetAdapterIdForChannel(channelId.ToString());
        if (adapterId == null)
            return Task.FromResult(new ToolResult { Status = "failed", Error = "当前频道没有可用的适配器" });

        var saveName = fileName ?? fileId[..Math.Min(fileId.Length, 16)];
        var safeName = GroupFileToolArgs.MakeSafeName(saveName, fileId[..Math.Min(fileId.Length, 16)]);
        var destDir = _workspace?.Resolve("Downloads");
        if (destDir == null)
            return Task.FromResult(new ToolResult { Status = "failed", Error = "工作区不可用" });
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, safeName);

        // 注册下载任务
        var store = _store;
        var task = store?.Register(_sessionId, saveName, destPath);
        var taskId = task?.Id ?? "";

        var adapterAccess = _adapterAccess;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await adapterAccess.ExecuteActionAsync(adapterId, "download_chat_file",
                    $"{{\"file_id\":\"{fileId}\",\"dest_path\":\"{JsonEscape(destPath)}\"}}");

                // 检查文件是否下载成功（适配器返回 null 表示 action 失败，错误原因已在线程内抛出）
                if (result != null && File.Exists(destPath))
                {
                    var size = new FileInfo(destPath).Length;
                    store?.MarkCompleted(taskId, size);
                }
                else
                {
                    store?.MarkFailed(taskId, "文件下载失败：目标文件未生成");
                }
            }
            catch (Exception ex)
            {
                store?.MarkFailed(taskId, ex.Message);
            }
        }, ct);

        return Task.FromResult(new ToolResult { Status = "success", Data = $"[下载已提交] {saveName}" });
    }

    /// <summary>JSON 字符串转义（路径含反斜杠/引号时不能直接拼进参数 JSON）。</summary>
    private static string JsonEscape(string s) => s
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"");
}
