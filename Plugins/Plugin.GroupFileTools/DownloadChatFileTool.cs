using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.GroupFileTools;

[ToolMeta(Group = null, ContinueLoop = true, ExpressAvailable = true, OutputOnly = true)]
public class DownloadChatFileTool : ITool
{
    private readonly IAdapterAccess? _adapterAccess;
    private readonly string _adapterId = "";
    private readonly string _workspaceDir = "";

    public DownloadChatFileTool() { }

    public DownloadChatFileTool(IAdapterAccess adapterAccess,
        string adapterId, string workspaceDir)
    {
        _adapterAccess = adapterAccess;
        _adapterId = adapterId;
        _workspaceDir = workspaceDir;
    }

    public string Name => "download_chat_file";
    public string Description => "下载私聊/群聊中直接发送的文件。"
        + "先从消息里的 [消息附件-文件] 获取 file_id，然后调用此工具下载。"
        + "参数: file_id(必填，来自消息附件的file_id), file_name(可选，期望的文件名)。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("file_id", "文件ID（从消息附件的file_id获取）", 0),
        new("file_name", "期望的文件名（可选）", 1, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (_adapterAccess == null)
            return new ToolResult { Status = "failed", Error = "适配器服务不可用" };

        var fileId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var fileName = resolvedInputs.Count > 1 ? resolvedInputs[1]?.Trim() : null;

        if (string.IsNullOrEmpty(fileId))
            return new ToolResult { Status = "failed", Error = "file_id 不能为空" };

        var saveName = fileName ?? fileId[..Math.Min(fileId.Length, 16)];
        var safeName = string.Join("_", saveName.Split(Path.GetInvalidFileNameChars()));
        var destDir = Path.Combine(_workspaceDir, "Downloads");
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, safeName);

        var adapterAccess = _adapterAccess;
        var adapterId = _adapterId;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await adapterAccess.ExecuteActionAsync(adapterId, "download_chat_file",
                    $"{{\"file_id\":\"{fileId}\",\"dest_path\":\"{destPath.Replace("\\", "\\\\")}\"}}");
                if (result == null)
                    throw new InvalidOperationException("下载失败");
            }
            catch { }
        }, ct);

        return new ToolResult { Status = "success", Data = $"[下载已提交] {saveName}" };
    }
}
