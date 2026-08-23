// Plugins/Plugin.HomeAssistant/Tools/HaGetHistoryTool.cs
using System.Text;
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.HomeAssistant.Tools;

/// <summary>
/// 查询 Home Assistant 历史状态（时间序列）。
/// GET /api/history/period/{timestamp}?filter_entity_id=xxx&minimal_response&no_attributes
/// 返回每个 state change 的时间 + 状态值，按实体分组精简展示。
/// </summary>
[ToolMeta(Group = "ha", ContinueLoop = true)]
public class HaGetHistoryTool : ITool
{
    private readonly HaApiClient _api;

    public string Name => "ha_get_history";
    public string Description => "查询 Home Assistant 实体历史状态（如温度曲线、开关记录）。"
        + "传 entity_id 和回看小时数（默认取配置值）。返回时间序列摘要，适合看趋势。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("entity_id", "实体ID，如 climate.wo_shi_kong_diao / sensor.xxx", 0),
        new("hours", "回看小时数（默认 6）", 1, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(_api.Config.TimeoutSeconds + 8);

    public HaGetHistoryTool(HaApiClient api) => _api = api;

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var entityId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        if (string.IsNullOrEmpty(entityId))
            return HaApiClient.Fail("entity_id 不能为空");

        var hours = _api.Config.HistoryHours;
        if (resolvedInputs.Count > 1 && int.TryParse(resolvedInputs[1].Trim(), out var h) && h > 0)
            hours = Math.Min(h, 24 * 7); // 上限一周

        var start = DateTimeOffset.UtcNow.AddHours(-hours).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var path = $"/api/history/period/{Uri.EscapeDataString(start)}" +
                   $"?filter_entity_id={Uri.EscapeDataString(entityId)}&minimal_response&no_attributes";

        var (code, body) = await _api.GetAsync(path, ct);
        if (code != 200) return HaApiClient.FailHttp(code, body);

        using var doc = JsonDocument.Parse(body);
        // 结构: [[{entity 状态变化...}, ...], ...]（按实体分组的多外层数组）
        var groups = doc.RootElement.EnumerateArray().ToList();
        var sb = new StringBuilder();
        var total = 0;
        foreach (var group in groups)
        {
            foreach (var item in group.EnumerateArray())
            {
                var ts = item.TryGetProperty("last_changed", out var t) ? t.GetString() : "";
                var st = item.TryGetProperty("state", out var s) ? s.GetString() : "";
                if (string.IsNullOrEmpty(ts)) continue;
                // 截取本地时间 HH:mm
                var local = DateTimeOffset.TryParse(ts, out var dto) ? dto.ToLocalTime().ToString("MM-dd HH:mm") : ts;
                total++;
                if (sb.Length < 4000)
                    sb.AppendLine($"{local}  {st}");
            }
        }
        if (total == 0)
            return HaApiClient.Fail($"实体 {entityId} 在最近 {hours} 小时内没有状态变化");

        var head = $"实体 {entityId} 最近 {hours}h 状态变化（共 {total} 条，时间均本地时区）:\n";
        return HaApiClient.Ok(head + (sb.Length > 6000 ? HaApiClient.Truncate(sb.ToString(), 6000) : sb.ToString().TrimEnd()));
    }
}