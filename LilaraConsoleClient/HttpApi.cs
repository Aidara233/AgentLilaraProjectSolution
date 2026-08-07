using System.Net.Http.Headers;
using System.Text.Json;

namespace LilaraConsoleClient;

/// <summary>
/// HttpBridge HTTP 层：非实时查询（历史/频道/状态）。端点见 docs/http-bridge-api.md §2。
/// </summary>
internal sealed class HttpApi
{
    private readonly HttpClient http;

    public HttpApi(string host, int port, string? token)
    {
        http = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}/") };
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>GET /api/channels — 频道列表。</summary>
    public async Task<IReadOnlyList<ChannelInfo>> GetChannelsAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("api/channels", ct);
        var list = new List<ChannelInfo>();
        if (json.TryGetProperty("channels", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray())
                list.Add(new ChannelInfo(it.GetProperty("id").GetInt32(), it.GetProperty("name").GetString() ?? ""));
        return list;
    }

    /// <summary>
    /// GET /api/messages — 聊天记录。afterId &gt; 0 时增量拉取该 id 之后的新消息（升序）。
    /// </summary>
    public async Task<IReadOnlyList<MessageInfo>> GetMessagesAsync(
        string channel, int limit = 50, int afterId = 0, CancellationToken ct = default)
    {
        var query = $"api/messages?channel={Uri.EscapeDataString(channel)}&limit={limit}";
        if (afterId > 0) query += $"&afterId={afterId}";
        var json = await GetJsonAsync(query, ct);
        var list = new List<MessageInfo>();
        if (json.TryGetProperty("messages", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var it in arr.EnumerateArray())
                list.Add(MessageInfo.FromJson(it));
        return list;
    }

    /// <summary>GET /api/status — 适配器 + 引擎状态。</summary>
    public async Task<StatusInfo?> GetStatusAsync(CancellationToken ct = default)
    {
        var json = await GetJsonAsync("api/status", ct);
        if (!json.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) return null;

        AdapterStatusInfo? adapter = null;
        if (json.TryGetProperty("adapter", out var a) && a.ValueKind == JsonValueKind.Object)
            adapter = new AdapterStatusInfo(
                a.GetProperty("id").GetString() ?? "",
                a.GetProperty("state").GetString() ?? "",
                a.TryGetProperty("port", out var p) ? p.GetInt32() : 0,
                a.TryGetProperty("defaultChannel", out var dc) ? dc.GetString() ?? "" : "");

        EngineStatusInfo? engine = null;
        if (json.TryGetProperty("engine", out var e) && e.ValueKind == JsonValueKind.Object)
            engine = new EngineStatusInfo(
                e.TryGetProperty("isIdle", out var idle) && idle.GetBoolean(),
                e.TryGetProperty("busyWorker", out var busy) && busy.GetBoolean());

        return new StatusInfo(adapter, engine);
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await http.GetAsync(path, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return doc.RootElement.Clone();
    }
}

internal sealed record ChannelInfo(int Id, string Name);

internal sealed record MessageInfo(
    int Id, int UserId, string SenderName, string Content, bool IsFromBot, string Time, string? PlatformMessageId)
{
    public static MessageInfo FromJson(JsonElement e) => new(
        e.GetProperty("id").GetInt32(),
        e.GetProperty("userId").GetInt32(),
        e.GetProperty("senderName").GetString() ?? "",
        e.GetProperty("content").GetString() ?? "",
        e.GetProperty("isFromBot").GetBoolean(),
        e.GetProperty("time").GetString() ?? "",
        e.TryGetProperty("platformMessageId", out var p) ? p.GetString() : null);
}

internal sealed record AdapterStatusInfo(string Id, string State, int Port, string DefaultChannel);

internal sealed record EngineStatusInfo(bool IsIdle, bool BusyWorker);

internal sealed record StatusInfo(AdapterStatusInfo? Adapter, EngineStatusInfo? Engine);
