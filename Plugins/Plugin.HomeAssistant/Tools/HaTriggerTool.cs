// Plugins/Plugin.HomeAssistant/Tools/HaTriggerTool.cs
using System.Text;
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.HomeAssistant.Tools;

/// <summary>
/// 触发 Home Assistant 自动化或脚本。
/// POST /api/services/script/{name} 或 /api/services/automation/{name}
/// 等价于 ha_call_service，但语义更明确、参数更少，给模型用更安全（明确是触发器）。
/// </summary>
[ToolMeta(Group = "ha", ContinueLoop = true)]
public class HaTriggerTool : ITool
{
    private readonly HaApiClient _api;

    public string Name => "ha_trigger";
    public string Description => "触发 Home Assistant 自动化或脚本。"
        + "传自动化/脚本实体ID（如 script.morning_routine / automation.night_mode）。"
        + "等价于调用 script.xxx / automation.xxx 服务。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("entity_id", "自动化或脚本实体ID，如 script.morning_routine", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(_api.Config.TimeoutSeconds + 5);

    public HaTriggerTool(HaApiClient api) => _api = api;

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var entityId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        if (string.IsNullOrEmpty(entityId))
            return HaApiClient.Fail("entity_id 不能为空");

        var domain = entityId.StartsWith("script.", StringComparison.OrdinalIgnoreCase) ? "script"
            : entityId.StartsWith("automation.", StringComparison.OrdinalIgnoreCase) ? "automation"
            : "";
        if (domain == "")
            return HaApiClient.Fail($"只支持触发 script.* 或 automation.*，收到: {entityId}");

        try
        {
            var name = entityId[(domain.Length + 1)..];
            var json = JsonSerializer.Serialize(new { entity_id = entityId });
            var (code, body) = await _api.PostAsync($"/api/services/{domain}/{Uri.EscapeDataString(name)}", json, ct);
            if (code != 200) return HaApiClient.FailHttp(code, body);

            return HaApiClient.Ok($"已触发 {entityId}");
        }
        catch (HaApiException ex)
        {
            return HaApiClient.Fail(ex.Message);
        }
    }
}