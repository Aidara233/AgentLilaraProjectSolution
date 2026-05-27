using System.Text.Json;

namespace Plugin.WebSearch;

public class WebSearchConfig
{
    public string Backend { get; set; } = "tavily";
    public TavilyConfig Tavily { get; set; } = new();

    public static WebSearchConfig Load(string configDir)
    {
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "WebSearch.json");

        if (!File.Exists(path))
        {
            var cfg = new WebSearchConfig();
            var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            return cfg;
        }

        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<WebSearchConfig>(text,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }
}

public class TavilyConfig
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.tavily.com/search";
    public string SearchDepth { get; set; } = "basic";
    public List<string> IncludeDomains { get; set; } = new();
    public List<string> ExcludeDomains { get; set; } = new();
    public int TimeoutSeconds { get; set; } = 30;
}
