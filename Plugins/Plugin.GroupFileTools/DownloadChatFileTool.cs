using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.GroupFileTools;

[ToolMeta(Group = null, ContinueLoop = true, ExpressAvailable = true, OutputOnly = true)]
public class DownloadChatFileTool : ITool
{
    private readonly IAdapterAccess? _adapterAccess;
    private readonly string _adapterId = "";
    private readonly string _workspaceDir = "";
    private readonly GroupFileDownloadStore? _store;
    private readonly string _loopId = "";

    public DownloadChatFileTool() { }

    public DownloadChatFileTool(IAdapterAccess adapterAccess,
        string adapterId, string workspaceDir,
        GroupFileDownloadStore store, string loopId)
    {
        _adapterAccess = adapterAccess;
        _adapterId = adapterId;
        _workspaceDir = workspaceDir;
        _store = store;
        _loopId = loopId;
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

        var saveName = fileName ?? fileId[..Math.Min(fileId.Length, 16)];
        var safeName = string.Join("_", saveName.Split(Path.GetInvalidFileNameChars()));
        var destDir = Path.Combine(_workspaceDir, "Downloads");
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, safeName);

        // 注册下载任务
        var store = _store;
        var task = store?.Register(_loopId, saveName, destPath);
        var taskId = task?.Id ?? "";

        var adapterAccess = _adapterAccess;
        var adapterId = _adapterId;
        _ = Task.Run(async () =>
        {
            try
            {
                await adapterAccess.ExecuteActionAsync(adapterId, "download_chat_file",
                    $"{{\"file_id\":\"{fileId}\",\"dest_path\":\"{destPath.Replace("\\", "\\\\")}\"}}");

                // 检查文件是否下载成功
                if (File.Exists(destPath))
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
}
