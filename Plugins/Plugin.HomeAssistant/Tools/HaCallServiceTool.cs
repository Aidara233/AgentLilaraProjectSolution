// Plugins/Plugin.HomeAssistant/Tools/HaCallServiceTool.cs
using System.Text;
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.HomeAssistant.Tools;

/// <summary>
/// 调用 Home Assistant 任意服务（写操作核心）。如气候调温、开关设备。
/// POST /api/services/{domain}/{service}，body 可带 entity_id + data。
/// </summary>
[ToolMeta(Group = "ha", ContinueLoop = true)]
public class HaCallServiceTool : ITool
{
    private readonly HaApiClient _api;

    public string Name => "ha_call_service";
    public string Description => "调用 Home Assistant 服务（写操作）。"
        + "例如空调调温：domain=climate, service=set_temperature, entity_id=climate.wo_shi_kong_diao, data={\"temperature\":26}。"
        + "常用服务: climate.set_temperature / light.turn_on / switch.toggle / script.xxx / automation.trigger。"
        + "data 为 JSON 字符串（可选）。执行后返回受影响实体状态。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("domain", "服务域，如 climate/light/switch/script/automation", 0),
        new("service", "服务名，如 set_temperature/turn_on/toggle/trigger", 1),
        new("entity_id", "目标实体ID（可选）", 2, isRequired: false),
        new("data", "JSON 格式附加参数，如 {\"temperature\":26}（可选）", 3, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(_api.Config.TimeoutSeconds + 5);

    public HaCallServiceTool(HaApiClient api) => _api = api;

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var domain = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var service = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";
        var entityId = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";
        var dataJson = resolvedInputs.Count > 3 ? resolvedInputs[3].Trim() : "";

        if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(service))
            return HaApiClient.Fail("domain 和 service 不能为空");

        // 校验 data 是合法 JSON（解析一次，同时用于发送）
        JsonElement? dataObj = null;
        if (!string.IsNullOrEmpty(dataJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(dataJson);
                dataObj = doc.RootElement.Clone();
            }
            catch
            {
                return HaApiClient.Fail($"data 不是合法 JSON: {dataJson}");
            }
        }

        // HA 服务调用期望扁平结构：{entity_id, temperature, ...}，不认嵌套 "data" 键。
        // 把 data 的每个属性展开并入 payload（同名键 data 覆盖 entity_id 之外，entity_id 优先取显式参数）。
        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrEmpty(entityId)) payload["entity_id"] = entityId;
        if (dataObj.HasValue && dataObj.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in dataObj.Value.EnumerateObject())
            {
                if (string.Equals(prop.Name, "entity_id", StringComparison.OrdinalIgnoreCase))
                    continue; // entity_id 以显式参数为准
                payload[prop.Name] = ParseJsonValue(prop.Value);
            }
        }

        var json = JsonSerializer.Serialize(payload);
        var (code, body) = await _api.PostAsync($"/api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}", json, ct);

        if (code != 200)
            return HaApiClient.FailHttp(code, body);

        // 解析返回的受影响实体，简洁展示
        try
        {
            using var doc2 = HaApiClient.ParseBody(body);
            var changed = doc2.RootElement.EnumerateArray().ToList();
            var sb = new StringBuilder();
            sb.AppendLine($"服务调用成功: {domain}.{service}");
            sb.AppendLine($"受影响实体 {changed.Count} 个:");
            foreach (var s in changed)
            {
                var id = s.TryGetProperty("entity_id", out var e) ? e.GetString() : "?";
                var state = s.TryGetProperty("state", out var st) ? st.GetString() : "?";
                sb.AppendLine($"- {id} → {state}");
            }
            return HaApiClient.Ok(sb.ToString().TrimEnd());
        }
        catch (HaApiException ex)
        {
            return HaApiClient.Fail(ex.Message);
        }
    }

    /// <summary>JsonElement → CLR 值（保留数字/布尔/数组类型，供 JsonSerializer 序列化）。</summary>
    private static object? ParseJsonValue(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Object => el.EnumerateObject()
                .ToDictionary(p => p.Name, p => ParseJsonValue(p.Value)),
            JsonValueKind.Array => el.EnumerateArray().Select(ParseJsonValue).ToList(),
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => el.GetRawText()
        };
    }
}