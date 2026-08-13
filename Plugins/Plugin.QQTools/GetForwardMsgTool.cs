// Plugins/Plugin.QQTools/GetForwardMsgTool.cs
using AgentLilara.PluginSDK;
using AgentLilara.PluginSDK.Services;

namespace Plugin.QQTools;

[ToolMeta(Group = null, ContinueLoop = true, OutputOnly = true)]
public class GetForwardMsgTool : ITool
{
    private readonly IAdapterAccess? _adapterAccess;
    private readonly string _adapterId = "";
    private readonly string _downloadsDir = "";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public GetForwardMsgTool() { }

    public GetForwardMsgTool(IAdapterAccess adapterAccess, string adapterId, string downloadsDir)
    {
        _adapterAccess = adapterAccess;
        _adapterId = adapterId;
        _downloadsDir = downloadsDir;
    }

    public string Name => "get_forward_msg";
    public string Description => "获取 QQ 合并转发（转发聊天记录）的完整内容。"
        + "参数 forward_id 填合并转发消息里的 id（如 7535115279178088873）。"
        + "返回按发送者展开的全部消息（含嵌套转发），以及参与人/条数/字数/图片数统计；"
        + "子消息图片会下载注入。内容可能很长，是否总结或派子agent由你判断。";

    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("forward_id", "合并转发的 id（消息统计行中的 id，如 7535115279178088873）", 0)
    ];

    public TimeSpan Timeout => TimeSpan.FromSeconds(20);

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        if (_adapterAccess == null)
            return new ToolResult { Status = "failed", Error = "适配器服务不可用" };

        var forwardId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        if (string.IsNullOrEmpty(forwardId))
            return new ToolResult { Status = "failed", Error = "forward_id 不能为空" };

        var result = await _adapterAccess.GetForwardMessageAsync(_adapterId, forwardId);
        if (result == null)
            return new ToolResult { Status = "failed", Error = $"获取合并转发失败（id={forwardId}，可能已失效或超时）" };

        var text = result.FullText;

        // 下载子消息图片注入多模态（尽力而为：仅 http(s) URL，失败降级为 [图片N] 文本）
        var attachments = new List<ContentAttachment>();
        if (result.ImageUrls.Count > 0)
        {
            try
            {
                Directory.CreateDirectory(_downloadsDir);
                for (int i = 0; i < result.ImageUrls.Count; i++)
                {
                    var url = result.ImageUrls[i];
                    if (string.IsNullOrEmpty(url)) continue;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                        continue; // 本地路径等不可下载，保留 [图片N] 文本标记

                    try
                    {
                        var ext = Path.GetExtension(uri.AbsolutePath);
                        if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".jpg";
                        var dest = Path.Combine(_downloadsDir, $"{forwardId}_{i + 1}{ext}");
                        var bytes = await _http.GetByteArrayAsync(url, ct);
                        await File.WriteAllBytesAsync(dest, bytes, ct);
                        attachments.Add(new ContentAttachment
                        {
                            Type = "image",
                            FilePath = dest,
                            MediaType = ext.ToLowerInvariant() switch
                            {
                                ".png" => "image/png",
                                ".gif" => "image/gif",
                                ".webp" => "image/webp",
                                ".bmp" => "image/bmp",
                                _ => "image/jpeg"
                            }
                        });
                    }
                    catch { /* 单张失败不影响整体，保留 [图片N] 文本 */ }
                }
            }
            catch { /* 下载基础设施失败不影响文本返回 */ }
        }

        return new ToolResult
        {
            Status = "success",
            Data = text,
            Attachments = attachments.Count > 0 ? attachments : null
        };
    }
}
