// Plugins/Plugin.HomeAssistant/HaFormatter.cs
using System.Text;
using System.Text.Json;

namespace Plugin.HomeAssistant;

/// <summary>
/// 把 HA JSON 状态转成给模型看的结构化中文摘要（树状缩进，不含时间戳/context/内部元数据）。
/// 目的是省 token：模型只需要「名称 → 状态/关键属性」，不需要原始 JSON。
/// </summary>
public static class HaFormatter
{
    /// <summary>格式化单个实体状态（/api/states/{id} 的根对象）。</summary>
    public static string FormatEntity(JsonElement root)
    {
        var sb = new StringBuilder();
        var id = root.TryGetProperty("entity_id", out var e) ? e.GetString() : "";
        var state = root.TryGetProperty("state", out var s) ? s.GetString() : "";
        var attrs = root.TryGetProperty("attributes", out var a) ? a : default;

        var name = GetAttribute(attrs, "friendly_name");
        var title = string.IsNullOrEmpty(name) ? id : name;

        sb.AppendLine(title);
        sb.AppendLine($"  状态: {FriendlyState(state)}");

        if (attrs.ValueKind == JsonValueKind.Object)
            AppendKeyAttrs(sb, attrs, id?.Split('.')[0], state);

        return sb.ToString().TrimEnd();
    }

    /// <summary>列表条目用：只输出关键属性（不含标题/状态行）。</summary>
    public static string FormatEntityListLine(JsonElement root)
    {
        var state = root.TryGetProperty("state", out var s) ? s.GetString() : "";
        var attrs = root.TryGetProperty("attributes", out var a) ? a : default;
        var tmp = new StringBuilder();
        if (attrs.ValueKind == JsonValueKind.Object)
        {
            var id = root.TryGetProperty("entity_id", out var e) ? e.GetString() : "";
            AppendKeyAttrs(tmp, attrs, id?.Split('.')[0], state);
        }
        return tmp.ToString().TrimEnd();
    }

    /// <summary>美化状态值：on/off → 开/关，空调模式 → 制冷/制热 等。</summary>
    public static string FriendlyState(string? state)
    {
        return state switch
        {
            "on" => "开",
            "off" => "关",
            "unavailable" => "不可用",
            "unknown" => "未知",
            "home" => "在家",
            "not_home" => "离家",
            "cool" => "制冷",
            "heat" => "制热",
            "dry" => "除湿",
            "fan_only" => "送风",
            "auto" => "自动",
            "idle" => "待机",
            _ => state ?? ""
        };
    }

    private static void AppendKeyAttrs(StringBuilder sb, JsonElement attrs, string? domain, string? state)
    {
        // 数值类属性（温度/湿度/电量等）统一先展示——climate/sensor 最关心
        foreach (var key in new[] { "temperature", "current_temperature", "humidity", "battery", "current" })
        {
            if (attrs.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                sb.AppendLine($"  {FriendlyKeyName(key)}: {v.GetRawText()}{GetUnit(key)}");
        }

        switch (domain)
        {
            case "climate":
                AppendStr(sb, attrs, "hvac_action", "运行", s => FriendlyState(s));
                AppendList(sb, attrs, "hvac_modes", "支持模式");
                AppendStr(sb, attrs, "fan_mode", "风速");
                AppendStr(sb, attrs, "preset_mode", "预设", s => s == "none" ? "无" : s);
                break;
            case "sensor":
                // 数值已在上面展示
                break;
            case "update":
                AppendStr(sb, attrs, "installed_version", "当前版本");
                AppendStr(sb, attrs, "latest_version", "最新版本");
                break;
            case "light":
            case "switch":
            case "binary_sensor":
                break; // 状态行已表达开关，无需额外
            case "weather":
                AppendStr(sb, attrs, "temperature_unit", "温度单位");
                break;
            default:
                AppendStr(sb, attrs, "device_class", "类型");
                break;
        }
    }

    private static void AppendStr(StringBuilder sb, JsonElement attrs, string key, string label, Func<string?, string?>? map = null)
    {
        if (attrs.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
        {
            var raw = v.GetString();
            var val = map != null ? map(raw) : raw;
            if (!string.IsNullOrEmpty(val))
                sb.AppendLine($"  {label}: {val}");
        }
    }

    private static void AppendList(StringBuilder sb, JsonElement attrs, string key, string label)
    {
        if (attrs.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            var items = v.EnumerateArray().Select(x => x.GetString()).Where(s => !string.IsNullOrEmpty(s));
            sb.AppendLine($"  {label}: {string.Join("/", items)}");
        }
    }

    private static string GetAttribute(JsonElement attrs, string key)
    {
        return attrs.ValueKind == JsonValueKind.Object && attrs.TryGetProperty(key, out var v)
            ? v.GetString() ?? "" : "";
    }

    private static string FriendlyKeyName(string key) => key switch
    {
        "temperature" => "温度",
        "current_temperature" => "当前室温",
        "humidity" => "湿度",
        "battery" => "电量",
        "current" => "电流",
        _ => key
    };

    private static string GetUnit(string key) => key switch
    {
        "temperature" => " °C",
        "current_temperature" => " °C",
        "humidity" => " %",
        "battery" => " %",
        _ => ""
    };
}