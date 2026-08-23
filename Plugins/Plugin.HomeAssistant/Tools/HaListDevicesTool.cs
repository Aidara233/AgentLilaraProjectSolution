// Plugins/Plugin.HomeAssistant/Tools/HaListDevicesTool.cs
using System.Text;
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.HomeAssistant.Tools;

/// <summary>
/// 列出 Home Assistant 设备。可选按域过滤。
/// 本质是 /api/states 的按域聚合列表，展示每个实体的 ID、状态、名称。
/// </summary>
[ToolMeta(Group = "ha", ContinueLoop = true)]
public class HaListDevicesTool : ITool
{
    private readonly HaApiClient _api;

    public string Name => "ha_list_devices";
    public string Description => "列出 Home Assistant 中的设备/实体。可选传 domain（climate/light/sensor/switch/script/automation 等）"
        + "只列该域，不传则列出所有域概览。适合总览家庭设备。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("domain", "实体域过滤（可选）", 0, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(_api.Config.TimeoutSeconds + 5);

    public HaListDevicesTool(HaApiClient api) => _api = api;

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var domain = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim().ToLowerInvariant() : "";

        try
        {
            var (code, body) = await _api.GetAsync("/api/states", ct);
            if (code != 200) return HaApiClient.FailHttp(code, body);

            using var doc = HaApiClient.ParseBody(body);
            var states = doc.RootElement.EnumerateArray().ToList();

            var processed = domain == "" ? states : states.Where(s =>
                s.GetProperty("entity_id").GetString()?.StartsWith(domain + ".") == true).ToList();

            if (processed.Count == 0)
                return HaApiClient.Fail($"没有找到{(domain == "" ? "任何" : $"域 {domain}")}的设备");

            var sb = new StringBuilder();
            if (domain == "")
            {
                var byDomain = processed.GroupBy(s => s.GetProperty("entity_id").GetString()?.Split('.')[0] ?? "?")
                    .OrderByDescending(g => g.Count());
                sb.AppendLine($"HA 设备/实体总数: {processed.Count}");
                foreach (var g in byDomain)
                    sb.AppendLine($"  {g.Key}: {g.Count()}");
            }
            else
            {
                sb.AppendLine($"[{domain}] 共 {processed.Count} 个:");
                foreach (var s in processed)
                {
                    var id = s.GetProperty("entity_id").GetString();
                    var state = s.GetProperty("state").GetString();
                    var name = s.TryGetProperty("attributes", out var a) && a.TryGetProperty("friendly_name", out var n)
                        ? n.GetString() : null;
                    sb.AppendLine($"- {id} → {state}{(string.IsNullOrEmpty(name) ? "" : $" ({name})")}");
                }
            }
            return HaApiClient.Ok(HaApiClient.Truncate(sb.ToString().TrimEnd(), 8000));
        }
        catch (HaApiException ex)
        {
            return HaApiClient.Fail(ex.Message);
        }
    }
}