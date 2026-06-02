using System.Text.Json;

namespace Plugin.CampusPrint;

public class CampusPrintConfig
{
    public string Token { get; set; } = "";
    public string Appkey { get; set; } = "";
    public int StoreId { get; set; } = 1440;
    public string UpUrl { get; set; } = "";
    public int DomainId { get; set; } = 2;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(Appkey);

    public static CampusPrintConfig Load(string configDir)
    {
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "CampusPrint.json");

        if (!File.Exists(path))
        {
            var cfg = new CampusPrintConfig();
            Save(cfg, path);
            return cfg;
        }

        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CampusPrintConfig>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch
        {
            return new CampusPrintConfig();
        }
    }

    public void Save(string configDir)
    {
        Save(this, Path.Combine(configDir, "CampusPrint.json"));
    }

    private static void Save(CampusPrintConfig cfg, string path)
    {
        var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }
}
