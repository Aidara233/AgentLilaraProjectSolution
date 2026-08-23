// Plugins/Plugin.HomeAssistant/Tools/HaGetStatesTool.cs
using System.Text;
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.HomeAssistant.Tools;

/// <summary>
/// 查询 Home Assistant 实体状态。
/// - 指定 entity_id：返回该实体状态 + 关键属性
/// - 指定 domain：返回该域所有实体（截断 + 汇总）
/// - 都不指定：返回全部实体摘要（仅领域计数 + 前若干条）
/// </summary>
[ToolMeta(Group = "ha", ContinueLoop = true)]
public class HaGetStatesTool : ITool
{
    private readonly HaApiClient _api;

    public string Name => "ha_get_states";
    public string Description => "查询 Home Assistant 实体状态。"
        + "可传 entity_id（如 climate.wo_shi_kong_diao）精确查单个，或传 domain（如 climate/light/sensor）查一类，"
        + "或都不传返回全局摘要。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("entity_id", "实体ID，如 climate.wo_shi_kong_diao（可选）", 0, isRequired: false),
        new("domain", "实体域，如 climate/light/sensor（可选）", 1, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(_api.Config.TimeoutSeconds + 5);

    public HaGetStatesTool(HaApiClient api) => _api = api;

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var entityId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var domain = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToLowerInvariant() : "";

        if (!string.IsNullOrEmpty(entityId))
            return await GetSingle(entityId, ct);

        var (code, body) = await _api.GetAsync("/api/states", ct);
        if (code != 200) return HaApiClient.FailHttp(code, body);

        using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { AllowTrailingCommas = true });
        var states = doc.RootElement.EnumerateArray().ToList();

        if (!string.IsNullOrEmpty(domain))
            return SummarizeDomain(states, domain);
        return SummarizeAll(states);
    }

    private async Task<ToolResult> GetSingle(string entityId, CancellationToken ct)
    {
        var (code, body) = await _api.GetAsync($"/api/states/{Uri.EscapeDataString(entityId)}", ct);
        if (code == 404) return HaApiClient.Fail($"实体不存在: {entityId}");
        if (code != 200) return HaApiClient.FailHttp(code, body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var sb = new StringBuilder();
        sb.AppendLine($"entity_id: {root.GetProperty("entity_id").GetString()}");
        sb.AppendLine($"state: {root.GetProperty("state").GetString()}");
        if (root.TryGetProperty("attributes", out var attrs))
        {
            var name = attrs.TryGetProperty("friendly_name", out var n) ? n.GetString() : null;
            if (!string.IsNullOrEmpty(name)) sb.AppendLine($"friendly_name: {name}");

            // 温控/传感器常用属性优先展示
            foreach (var key in new[] { "temperature", "current_temperature", "hvac_action",
                "hvac_modes", "fan_mode", "preset_mode", "unit_of_measurement", "device_class" })
            {
                if (attrs.TryGetProperty(key, out var v))
                    sb.AppendLine($"{key}: {v}");
            }
        }
        return HaApiClient.Ok(sb.ToString().TrimEnd());
    }

    private static ToolResult SummarizeDomain(List<JsonElement> states, string domain)
    {
        var matched = states.Where(s => s.GetProperty("entity_id").GetString()?.StartsWith(domain + ".") == true).ToList();
        if (matched.Count == 0)
            return HaApiClient.Fail($"域 {domain} 下没有实体");

        var sb = new StringBuilder();
        sb.AppendLine($"[{domain}] 共 {matched.Count} 个实体:");
        foreach (var s in matched)
        {
            var id = s.GetProperty("entity_id").GetString();
            var state = s.GetProperty("state").GetString();
            var name = s.TryGetProperty("attributes", out var a) && a.TryGetProperty("friendly_name", out var n)
                ? n.GetString() : null;
            sb.AppendLine($"- {id}  → {state}{(string.IsNullOrEmpty(name) ? "" : $"  ({name})")}");
        }
        return HaApiClient.Ok(HaApiClient.Truncate(sb.ToString().TrimEnd(), 6000));
    }

    private static ToolResult SummarizeAll(List<JsonElement> states)
    {
        var byDomain = states.GroupBy(s => s.GetProperty("entity_id").GetString()?.Split('.')[0] ?? "?")
            .OrderByDescending(g => g.Count());
        var sb = new StringBuilder();
        sb.AppendLine($"HA 实体总数: {states.Count}");
        sb.AppendLine("按域计数:");
        foreach (var g in byDomain)
            sb.AppendLine($"  {g.Key}: {g.Count()}");
        return HaApiClient.Ok(HaApiClient.Truncate(sb.ToString().TrimEnd(), 4000));
    }
}