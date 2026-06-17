using System.Text.Json;
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "browser", ContinueLoop = true)]
public class BrowserGetCookiesTool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public string Name => "browser_get_cookies";
    public string Description => "获取浏览器Cookie。可指定URL过滤，省略则返回所有Cookie。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("url", "URL过滤（可选，省略则获取所有Cookie）", 0, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public BrowserGetCookiesTool(BrowserComponent component, SecurityConfig security, string loopId)
    {
        _component = component;
        _security = security;
        _loopId = loopId;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var url = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";

        try
        {
            var sessionManager = _component.GetSessionManager();

            if (sessionManager == null)
                return Fail("浏览器组件未初始化");

            var session = await sessionManager.GetOrCreateSessionAsync(_loopId);

            // 获取 Cookie
            var cookies = string.IsNullOrEmpty(url)
                ? await session.Context.CookiesAsync()
                : await session.Context.CookiesAsync(new[] { url });

            var result = new
            {
                status = "success",
                cookies = cookies.Select(c => new
                {
                    name = c.Name,
                    value = c.Value,
                    domain = c.Domain,
                    path = c.Path,
                    expires = c.Expires,
                    httpOnly = c.HttpOnly,
                    secure = c.Secure,
                    sameSite = c.SameSite.ToString()
                }).ToList()
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (PlaywrightException ex)
        {
            return Fail($"获取Cookie失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
