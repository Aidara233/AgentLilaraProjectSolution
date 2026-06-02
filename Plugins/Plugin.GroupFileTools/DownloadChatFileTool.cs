using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.GroupFileTools;

[ToolMeta(Group = null, ContinueLoop = true, ExpressAvailable = true, OutputOnly = true)]
public class DownloadChatFileTool : ITool
{
    private readonly IAdapterAccess? _adapterAccess;
    private readonly IChannelAccess? _channelAccess;
    private readonly string _adapterId = "";
    private readonly int _channelId;
    private readonly string _workspaceDir = "";
    private readonly HttpClient _http;

    public DownloadChatFileTool() { _http = new HttpClient(); }

    public DownloadChatFileTool(IAdapterAccess adapterAccess, IChannelAccess channelAccess,
        string adapterId, int channelId, string workspaceDir, HttpClient http)
    {
        _adapterAccess = adapterAccess;
        _channelAccess = channelAccess;
        _adapterId = adapterId;
        _channelId = channelId;
        _workspaceDir = workspaceDir;
        _http = http;
    }

    public string Name => "download_chat_file";
    public string Description => "下载私聊/群聊中直接发送的文件。"
        + "先从消息里的 [消息附件-文件] 获取 file_id，然后调用此工具下载。"
        + "参数: file_id(必填，来自消息附件的file_id), file_name(可选，期望的文件名)。"
        + "文件较大时会后台下载并完成后通知。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("file_id", "文件ID（从消息附件的file_id获取）", 0),
        new("file_name", "期望的文件名（可选，用于完成通知）", 1, isRequired: false)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(15);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (_adapterAccess == null)
            return new ToolResult { Status = "failed", Error = "适配器服务不可用" };

        var fileId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var fileName = resolvedInputs.Count > 1 ? resolvedInputs[1]?.Trim() : null;

        if (string.IsNullOrEmpty(fileId))
            return new ToolResult { Status = "failed", Error = "file_id 不能为空" };

        // 通过 NapCat get_file API 获取下载 URL
        var url = await _adapterAccess.ExecuteActionAsync(_adapterId, "get_chat_file_url",
            $"{{\"file_id\":\"{fileId}\"}}");
        if (string.IsNullOrEmpty(url))
            return new ToolResult { Status = "failed", Error = "获取下载链接失败，请确认 file_id 正确且文件未过期" };

        // 后台下载
        var saveName = fileName ?? fileId[..Math.Min(fileId.Length, 16)];
        var safeName = string.Join("_", saveName.Split(Path.GetInvalidFileNameChars()));
        var destDir = Path.Combine(_workspaceDir, "Downloads");
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, safeName);

        var isBase64 = url.StartsWith("base64:", StringComparison.Ordinal);
        var http = _http;
        var channelAccess = _channelAccess;
        var channelId = _channelId;
        _ = Task.Run(async () =>
        {
            try
            {
                if (isBase64)
                {
                    var b64 = url["base64:".Length..];
                    var bytes = Convert.FromBase64String(b64);
                    await File.WriteAllBytesAsync(destPath, bytes);
                }
                else
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
