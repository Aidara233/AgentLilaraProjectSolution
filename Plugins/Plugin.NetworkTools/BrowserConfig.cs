using System.Text.Json;

namespace Plugin.NetworkTools;

public class BrowserConfig
{
    public string FallbackBrowserPath { get; set; } = "D:\\Playwright-browsers";
    public int DefaultTimeout { get; set; } = 30000;
    public int ViewportWidth { get; set; } = 1920;
    public int ViewportHeight { get; set; } = 1080;
    public string UserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36";
    public int MaxConcurrentContexts { get; set; } = 5;
    public int ContextIdleTimeout { get; set; } = 1800000;
    public bool HeadlessMode { get; set; } = true;
    public bool EnableScreenshots { get; set; } = true;
    public int SlowMo { get; set; } = 0;
    public int JavaScriptTimeout { get; set; } = 5000;

    public static BrowserConfig Load(string path)
    {
        if (!File.Exists(path))
            return new BrowserConfig();

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BrowserConfig>(json) ?? new BrowserConfig();
    }
}
