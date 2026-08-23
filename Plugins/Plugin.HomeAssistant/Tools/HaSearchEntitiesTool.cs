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

        try
        {
            var (code, body) = await _api.GetAsync("/api/states", ct);
            if (code != 200) return HaApiClient.FailHttp(code, body);

            using var doc = HaApiClient.ParseBody(body);
            var matched = new List<JsonElement>();
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                var id = s.GetProperty("entity_id").GetString() ?? "";
                var state = s.GetProperty("state").GetString() ?? "";
                var name = s.TryGetProperty("attributes", out var a) && a.TryGetProperty("friendly_name", out var n)
                    ? n.GetString() : null;

                if (id.ToLowerInvariant().Contains(query) || name?.ToLowerInvariant().Contains(query) == true)
                    matched.Add(s);
            }

            if (matched.Count == 0)
                return HaApiClient.Fail($"没有找到匹配'{query}'的实体");

            var sb = new StringBuilder();
            sb.AppendLine($"找到 {matched.Count} 个匹配'{query}'的实体:");
            foreach (var s in matched.Take(30))
            {
                var name2 = s.TryGetProperty("attributes", out var a2) && a2.TryGetProperty("friendly_name", out var n2)
                    ? n2.GetString() : null;
                var id2 = s.GetProperty("entity_id").GetString();
                var st2 = HaFormatter.FriendlyState(s.TryGetProperty("state", out var ss) ? ss.GetString() : "");
                sb.AppendLine($"{(!string.IsNullOrEmpty(name2) ? name2 : id2)}（{id2}） → {st2}");
            }
            if (matched.Count > 30)
                sb.AppendLine($"... 共 {matched.Count} 个，已截断");
            return HaApiClient.Ok(sb.ToString().TrimEnd());
        }
        catch (HaApiException ex)
        {
            return HaApiClient.Fail(ex.Message);
        }
    }
}