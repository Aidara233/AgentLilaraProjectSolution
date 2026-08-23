// Plugins/Plugin.HomeAssistant/HaConfig.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentLilara.PluginSDK;

namespace Plugin.HomeAssistant;

/// <summary>
/// Home Assistant 连接配置。懒加载到 Global 组件所在目录的 HaConfig.json。
/// Host 默认 homeassistant.lan（同主机名解析），Port 默认 80（实测 8123 关闭、80 反代开放）。
/// </summary>
public class HaConfig
{
    /// <summary>HA 主机名或 IP。</summary>
    public string Host { get; set; } = "homeassistant.lan";

    /// <summary>HTTP 端口（默认 80）。</summary>
    public int Port { get; set; } = 80;

    /// <summary>使用 HTTPS（默认 false）。</summary>
    public bool UseHttps { get; set; } = false;

    /// <summary>长期访问令牌（HA 个人资料→安全→长期访问令牌 生成）。不可留空。</summary>
    public string Token { get; set; } = "";

    /// <summary>HA 单次响应防呆上限（字节）。超过则拒绝并提示改用精确查询，不做字节截断（保护 JSON 完整性）。</summary>
    public int MaxResponseBytes { get; set; } = 512000;

    /// <summary>HTTP 超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>历史查询默认回看小时数。</summary>
    public int HistoryHours { get; set; } = 6;

    [JsonIgnore]
    public string BaseUrl => $"{(UseHttps ? "https" : "http")}://{Host}:{Port}";

    public static HaConfig Load(IPluginStorage storage)
    {
        var dir = storage.GlobalDirectory;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "HaConfig.json");

        if (!File.Exists(path))
        {
            var defaults = new HaConfig();
            var defaultJson = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, defaultJson);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<HaConfig>(json, options) ?? new HaConfig();
        }
        catch
        {
            return new HaConfig();
        }
    }
}