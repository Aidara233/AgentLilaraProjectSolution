using System.Text.Json;
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "browser", ContinueLoop = true)]
public class BrowserSetCookiesTool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public string Name => "browser_set_cookies";
    public string Description => "设置浏览器Cookie。接收JSON格式的Cookie数组。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("cookies", "Cookie数组（JSON格式）", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public BrowserSetCookiesTool(BrowserComponent component, SecurityConfig security, string loopId)
    {
        _component = component;
        _security = security;
        _loopId = loopId;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var cookiesJson = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";

        if (string.IsNullOrEmpty(cookiesJson))
            return Fail("cookies 不能为空");

        try
        {
            var sessionManager = _component.GetSessionManager();

            if (sessionManager == null)
                return Fail("浏览器组件未初始化");

            var session = await sessionManager.GetOrCreateSessionAsync(_loopId);

            // 解析 Cookie 数组
            var cookies = JsonSerializer.Deserialize<List<Cookie>>(cookiesJson);
            if (cookies == null || cookies.Count == 0)
                return Fail("Cookie数组为空或格式错误");

            // 设置 Cookie
            await session.Context.AddCookiesAsync(cookies);

            var result = new
            {
                status = "success",
                set_count = cookies.Count
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (JsonException ex)
        {
            return Fail($"Cookie JSON解析失败: {ex.Message}");
        }
        catch (PlaywrightException ex)
        {
            return Fail($"设置Cookie失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
