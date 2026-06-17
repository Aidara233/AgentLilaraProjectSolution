using System.Text.Json;
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "browser", ContinueLoop = true)]
public class BrowserNavigateTool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public string Name => "browser_navigate";
    public string Description => "导航到指定URL。支持等待策略：load（默认）/domcontentloaded/networkidle。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("url", "目标URL", 0),
        new("wait_until", "等待策略：load/domcontentloaded/networkidle，默认load（可选）", 1, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public BrowserNavigateTool(BrowserComponent component, SecurityConfig security, string loopId)
    {
        _component = component;
        _security = security;
        _loopId = loopId;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var url = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var waitUntil = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToLower() : "load";

        if (string.IsNullOrEmpty(url))
            return Fail("url 不能为空");

        // 安全校验
        var reject = _security.ValidateUrl(url);
        if (reject != null) return Fail(reject);

        var host = new Uri(url).Host;
        reject = _security.ValidateIp(host);
        if (reject != null) return Fail(reject);

        // 解析等待策略
        var waitUntilState = waitUntil switch
        {
            "domcontentloaded" => WaitUntilState.DOMContentLoaded,
            "networkidle" => WaitUntilState.NetworkIdle,
            _ => WaitUntilState.Load
        };

        try
        {
            var sessionManager = _component.GetSessionManager();
            var config = _component.GetConfig();

            if (sessionManager == null || config == null)
                return Fail("浏览器组件未初始化");

            var session = await sessionManager.GetOrCreateSessionAsync(_loopId);

            if (session.CurrentPage == null)
                session.CurrentPage = await session.Context.NewPageAsync();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await session.CurrentPage.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = waitUntilState,
                Timeout = config.DefaultTimeout
            });
            sw.Stop();

            var title = await session.CurrentPage.TitleAsync();

            var result = new
            {
                status = "success",
                url,
                title,
                load_time_ms = sw.ElapsedMilliseconds
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (PlaywrightException ex)
        {
            return Fail($"导航失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
