// Plugins/Plugin.NetworkTools/HttpRequestTool.cs
using System.Net;
using System.Text;
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "network", ContinueLoop = true, CapabilitySummary = "发送HTTP请求获取网页/API响应")]
public class HttpRequestTool : ITool
{
    private readonly HttpClient _http;
    private readonly SecurityConfig _security;

    public string Name => "http_request";
    public string Description => "发送HTTP请求并返回响应文本。支持GET/POST/PUT/DELETE/PATCH。"
        + "响应体超过限制时会截断，大文件请用download_file。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("url", "请求URL", 0),
        new("method", "HTTP方法，默认GET（可选）", 1, isRequired: false),
        new("headers", "JSON格式请求头，如{\"Authorization\":\"Bearer xxx\"}（可选）", 2, isRequired: false),
        new("body", "请求体文本（可选）", 3, isRequired: false),
        new("timeout", "超时秒数，默认取配置值（可选）", 4, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(_security.DefaultTimeoutSeconds + 5);

    public HttpRequestTool(HttpClient http, SecurityConfig security)
    {
        _http = http;
        _security = security;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var url = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var method = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToUpper() : "GET";
        var headersJson = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";
        var body = resolvedInputs.Count > 3 ? resolvedInputs[3] : "";
        var timeoutStr = resolvedInputs.Count > 4 ? resolvedInputs[4].Trim() : "";

        if (string.IsNullOrEmpty(url))
            return Fail("url不能为空");

        // 安全校验
        var reject = _security.ValidateUrl(url);
        if (reject != null) return Fail(reject);

        var host = new Uri(url).Host;
        reject = _security.ValidateIp(host);
        if (reject != null) return Fail(reject);

        // 构建请求
        var validMethods = new HashSet<string> { "GET", "POST", "PUT", "DELETE", "PATCH" };
        if (!validMethods.Contains(method))
            return Fail($"不支持的HTTP方法: {method}，支持: GET/POST/PUT/DELETE/PATCH");

        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        if (!string.IsNullOrEmpty(body) && method is "POST" or "PUT" or "PATCH")
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        if (!string.IsNullOrEmpty(headersJson))
        {
            try
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                if (headers != null)
                {
                    foreach (var (key, value) in headers)
                        request.Headers.TryAddWithoutValidation(key, value);
                }
            }
            catch { return Fail("headers格式无效，需要JSON对象"); }
        }

        // 超时
        var timeoutSeconds = _security.DefaultTimeoutSeconds;
        if (int.TryParse(timeoutStr, out var t) && t > 0)
            timeoutSeconds = Math.Min(t, 120);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);

            var respHeaders = new Dictionary<string, string>();
            foreach (var h in response.Headers)
                respHeaders[h.Key] = string.Join(", ", h.Value);
            foreach (var h in response.Content.Headers)
                respHeaders[h.Key] = string.Join(", ", h.Value);

            var rawBytes = await response.Content.ReadAsByteArrayAsync(linkedCts.Token);
            var maxBytes = _security.MaxResponseBodyBytes;
            var truncated = rawBytes.Length > maxBytes;
            var display = Encoding.UTF8.GetString(rawBytes, 0, truncated ? maxBytes : rawBytes.Length);

            if (truncated)
                display += $"\n\n[已截断: {rawBytes.Length} bytes → {maxBytes} bytes，请用download_file获取完整内容]";

            var result = new Dictionary<string, object>
            {
                ["status_code"] = (int)response.StatusCode,
                ["headers"] = respHeaders,
                ["body"] = display,
                ["size"] = rawBytes.Length,
                ["truncated"] = truncated
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fail($"请求超时（{timeoutSeconds}s）");
        }
        catch (HttpRequestException ex)
        {
            return Fail($"请求失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
