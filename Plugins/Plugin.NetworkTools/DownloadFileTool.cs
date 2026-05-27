// Plugins/Plugin.NetworkTools/DownloadFileTool.cs
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "network", ContinueLoop = true, CapabilitySummary = "异步下载文件到Workspace")]
public class DownloadFileTool : ITool
{
    private readonly HttpClient _http;
    private readonly SecurityConfig _security;
    private readonly DownloadStore _store;
    private readonly string _workspaceDir;
    private readonly string _loopId;

    public string Name => "download_file";
    public string Description => "启动异步文件下载，立即返回download_id。"
        + "文件保存到Agent指定的Workspace相对路径。下载完成后会通知Agent。"
        + "用list_downloads查看进度，cancel_download取消下载。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("url", "下载URL", 0),
        new("save_path", "保存路径，相对于Workspace目录", 1),
        new("headers", "JSON格式请求头（可选）", 2, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public DownloadFileTool(HttpClient http, SecurityConfig security, DownloadStore store,
        string workspaceDir, string loopId)
    {
        _http = http;
        _security = security;
        _store = store;
        _workspaceDir = workspaceDir;
        _loopId = loopId;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var url = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var savePath = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
        var headersJson = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";

        if (string.IsNullOrEmpty(url))
            return Fail("url不能为空");
        if (string.IsNullOrEmpty(savePath))
            return Fail("save_path不能为空");

        // 安全校验
        var reject = _security.ValidateUrl(url);
        if (reject != null) return Fail(reject);

        var host = new Uri(url).Host;
        reject = _security.ValidateIp(host);
        if (reject != null) return Fail(reject);

        // 解析保存路径
        var fullPath = Path.GetFullPath(Path.Combine(_workspaceDir, savePath));
        if (!fullPath.StartsWith(_workspaceDir, StringComparison.OrdinalIgnoreCase))
            return Fail("路径不合法：不能访问Workspace目录之外");

        // 提取文件名
        var fileName = Path.GetFileName(savePath);
        if (string.IsNullOrEmpty(fileName))
            fileName = Path.GetFileName(new Uri(url).AbsolutePath);
        if (string.IsNullOrEmpty(fileName))
            fileName = "download";

        // 解析headers
        Dictionary<string, string>? headers = null;
        if (!string.IsNullOrEmpty(headersJson))
        {
            try { headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson); }
            catch { return Fail("headers格式无效"); }
        }

        // 提前创建目录（避免后台任务同步异常导致进程崩溃）
        var dir = Path.GetDirectoryName(fullPath)!;
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { return Fail($"无法创建目录: {ex.Message}"); }

        // 注册下载任务
        var cts = new CancellationTokenSource();
        var task = new DownloadTask
        {
            Id = Guid.NewGuid().ToString()[..8],
            Url = url,
            SavePath = fullPath,
            RelativePath = savePath,
            LoopId = _loopId,
            FileName = fileName,
            Cts = cts
        };

        if (!_store.TryAdd(task))
        {
            cts.Dispose();
            return Fail($"已达到最大并发下载数({_store.MaxConcurrent})");
        }

        // 后台启动下载
        _ = DownloadAsync(task, headers, _http, _store, _security.ChunkSizeBytes);

        return Ok(JsonSerializer.Serialize(new
        {
            download_id = task.Id,
            status = "started",
            save_path = savePath,
            file_name = fileName
        }));
    }

    /// <summary>后台下载循环，不阻塞工具返回。</summary>
    private static async Task DownloadAsync(DownloadTask task, Dictionary<string, string>? headers,
        HttpClient http, DownloadStore store, int chunkSize)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, task.Url);
            if (headers != null)
            {
                foreach (var (key, value) in headers)
                    request.Headers.TryAddWithoutValidation(key, value);
            }

            using var response = await http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, task.Cts!.Token);
            response.EnsureSuccessStatusCode();

            task.TotalBytes = response.Content.Headers.ContentLength;

            // 从Content-Disposition提取文件名（如果URL没有给出有意义的名字）
            var cd = response.Content.Headers.ContentDisposition;
            if (cd?.FileName != null)
                task.FileName = cd.FileName.Trim('"');

            await using var stream = await response.Content.ReadAsStreamAsync(task.Cts.Token);
            await using var fileStream = new FileStream(task.SavePath, FileMode.Create,
                FileAccess.Write, FileShare.None, chunkSize, useAsync: true);

            var buffer = new byte[chunkSize];
            int bytesRead;
            long total = 0;

            while ((bytesRead = await stream.ReadAsync(buffer, task.Cts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), task.Cts.Token);
                total += bytesRead;
                task.BytesDownloaded = total;
            }

            store.MarkCompleted(task, total);
        }
        catch (OperationCanceledException)
        {
            // 用户调用cancel_download，已在store.Cancel中处理
        }
        catch (Exception ex)
        {
            store.MarkFailed(task, ex.Message);
        }
        finally
        {
            task.Cts?.Dispose();
            task.Cts = null;
        }
    }

    private static Task<ToolResult> Fail(string err) =>
        Task.FromResult(new ToolResult { Status = "failed", Error = err });
    private static Task<ToolResult> Ok(string data) =>
        Task.FromResult(new ToolResult { Status = "success", Data = data });
}
