// Plugins/Plugin.HomeAssistant/HaApiClient.cs
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentLilara.PluginSDK;

namespace Plugin.HomeAssistant;

/// <summary>
/// Home Assistant REST API 薄封装。只依赖共享 HttpClient（Global 组件提供）。
/// 完整读取原始 JSON（不截断、不因体积拒绝），供各工具解析成成形摘要后返回。
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
        // 完整读取。绝不在字节体积上拒绝或截断原始响应——它只是给工具解析的中间产物，
        // 给模型的是解析后的成形中文摘要（那一步才做展示截断）。体积大不代表该拒绝查询。
        var raw = await resp.Content.ReadAsByteArrayAsync(ct);
        return Encoding.UTF8.GetString(raw);
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + $"\n[已截断: {s.Length} → {max} 字符]";

    /// <summary>解析 HA 响应 JSON。失败抛 HaApiException（携带响应体开头，便于排查）。</summary>
    public static JsonDocument ParseBody(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            var head = body.Length > 200 ? body[..200] : body;
            throw new HaApiException($"HA 响应不是合法 JSON: {ex.Message} | 响应开头: {head}");
        }
    }

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