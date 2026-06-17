using System.Text.Json;
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "browser", ContinueLoop = true)]
public class BrowserWaitForTool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public string Name => "browser_wait_for";
    public string Description => "等待元素达到指定状态。状态包括：visible（可见）/hidden（隐藏）/attached（存在）/detached（移除）。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("selector", "元素选择器", 0),
        new("state", "等待状态：visible/hidden/attached/detached，默认visible（可选）", 1, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public BrowserWaitForTool(BrowserComponent component, SecurityConfig security, string loopId)
    {
        _component = component;
        _security = security;
        _loopId = loopId;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var selector = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var state = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToLower() : "visible";

        if (string.IsNullOrEmpty(selector))
            return Fail("selector 不能为空");

        var waitForState = state switch
        {
            "hidden" => WaitForSelectorState.Hidden,
            "attached" => WaitForSelectorState.Attached,
            "detached" => WaitForSelectorState.Detached,
            _ => WaitForSelectorState.Visible
        };

        try
        {
            var sessionManager = _component.GetSessionManager();
            var config = _component.GetConfig();

            if (sessionManager == null || config == null)
                return Fail("浏览器组件未初始化");

            var session = await sessionManager.GetOrCreateSessionAsync(_loopId);

            if (session.CurrentPage == null)
                return Fail("当前无活动页面，请先使用 browser_navigate");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await session.CurrentPage.WaitForSelectorAsync(selector, new PageWaitForSelectorOptions
            {
                State = waitForState,
                Timeout = config.DefaultTimeout
            });
            sw.Stop();

            var result = new
            {
                status = "success",
                selector,
                state,
                wait_time_ms = sw.ElapsedMilliseconds
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (PlaywrightException ex)
        {
            return Fail($"等待失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
