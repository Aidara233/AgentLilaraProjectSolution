// Plugins/Plugin.HomeAssistant/HaApiClient.cs
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;

namespace Plugin.HomeAssistant;

/// <summary>
/// Home Assistant REST API 薄封装。只依赖共享 HttpClient（Global 组件提供）。
/// 返回原始 JSON 字符串（已按 MaxResponseBytes 截断），供各工具二次加工。
/// </summary>
public class HaApiClient
{
    private readonly HttpClient _http;
    private readonly HaConfig _config;

    public HaApiClient(HttpClient http, HaConfig config)
    {
        _http = http;
        _config = config;
    }

    public HaConfig Config => _config;

    /// <summary>GET。返回 (statusCode, body)。请求/网络错误抛 HaApiException。</summary>
    public async Task<(int, string)> GetAsync(string path, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"{_config.BaseUrl}{path}", ct);
        var body = await ReadBodyAsync(resp, ct);
        return ((int)resp.StatusCode, body);
    }

    /// <summary>POST JSON。返回 (statusCode, body)。请求/网络错误抛 HaApiException。</summary>
    public async Task<(int, string)> PostAsync(string path, string json, CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"{_config.BaseUrl}{path}",
            new StringContent(json, Encoding.UTF8, "application/json"), ct);
        var body = await ReadBodyAsync(resp, ct);
        return ((int)resp.StatusCode, body);
    }

    private async Task<string> ReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var raw = await resp.Content.ReadAsByteArrayAsync(ct);
        var max = _config.MaxResponseBytes;
        var text = Encoding.UTF8.GetString(raw, 0, Math.Min(raw.Length, max));
        if (raw.Length > max)
            text += $"\n[已截断: {raw.Length} bytes → {max} bytes]";
        return text;
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + $"\n[已截断: {s.Length} → {max} 字符]";

    public static ToolResult Ok(string data) => new() { Status = "success", Data = data };
    public static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    public static ToolResult FailHttp(int status, string body)
    {
        var detail = body.Length > 300 ? body[..300] : body;
        return new ToolResult
        {
            Status = "failed",
            Error = $"HA HTTP {status}: {detail}"
        };
    }
}

/// <summary>HA API 请求/网络错误。</summary>
public class HaApiException(string message) : Exception(message);