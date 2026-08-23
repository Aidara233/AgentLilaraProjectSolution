// Plugins/Plugin.HomeAssistant/Tools/HaSearchEntitiesTool.cs
using System.Text;
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.HomeAssistant.Tools;

/// <summary>
/// 按关键词搜索实体（匹配 entity_id 或 friendly_name 子串）。
/// 用于在不确定确切 ID 时定位实体。
/// </summary>
[ToolMeta(Group = "ha", ContinueLoop = true)]
public class HaSearchEntitiesTool : ITool
{
    private readonly HaApiClient _api;

    public string Name => "ha_search_entities";
    public string Description => "在 Home Assistant 中按关键词搜索实体（匹配 entity_id 或中文名）。"
        + "例如查'空调'、'客厅'、'温度'。返回所有匹配项及当前状态。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("query", "搜索关键词，匹配实体ID或友好名称", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(_api.Config.TimeoutSeconds + 5);

    public HaSearchEntitiesTool(HaApiClient api) => _api = api;

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var query = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim().ToLowerInvariant() : "";
        if (string.IsNullOrEmpty(query))
            return HaApiClient.Fail("query 不能为空");

        var (code, body) = await _api.GetAsync("/api/states", ct);
        if (code != 200) return HaApiClient.FailHttp(code, body);

        using var doc = JsonDocument.Parse(body);
        var matched = new List<(string Id, string State, string? Name)>();
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            var id = s.GetProperty("entity_id").GetString() ?? "";
            var state = s.GetProperty("state").GetString() ?? "";
            var name = s.TryGetProperty("attributes", out var a) && a.TryGetProperty("friendly_name", out var n)
                ? n.GetString() : null;

            if (id.ToLowerInvariant().Contains(query) || name?.ToLowerInvariant().Contains(query) == true)
                matched.Add((id, state, name));
        }

        if (matched.Count == 0)
            return HaApiClient.Fail($"没有找到匹配'{query}'的实体");

        var sb = new StringBuilder();
        sb.AppendLine($"找到 {matched.Count} 个匹配'{query}'的实体:");
        foreach (var (id, state, name) in matched.Take(50))
            sb.AppendLine($"- {id} → {state}{(string.IsNullOrEmpty(name) ? "" : $" ({name})")}");
        if (matched.Count > 50)
            sb.AppendLine($"... 共 {matched.Count} 个，已截断");
        return HaApiClient.Ok(sb.ToString().TrimEnd());
    }
}