using System.Text.Json;
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "browser", ContinueLoop = true)]
public class BrowserClickTool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public string Name => "browser_click";
    public string Description => "点击页面元素。支持CSS选择器或文本选择器。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("selector", "元素选择器（CSS或文本）", 0),
        new("button", "鼠标按键：left/right/middle，默认left（可选）", 1, isRequired: false),
        new("click_count", "点击次数，默认1（可选）", 2, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public BrowserClickTool(BrowserComponent component, SecurityConfig security, string loopId)
    {
        _component = component;
        _security = security;
        _loopId = loopId;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var selector = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var button = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToLower() : "left";
        var clickCountStr = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "1";

        if (string.IsNullOrEmpty(selector))
            return Fail("selector 不能为空");

        if (!int.TryParse(clickCountStr, out var clickCount) || clickCount < 1)
            clickCount = 1;

        var mouseButton = button switch
        {
            "right" => MouseButton.Right,
            "middle" => MouseButton.Middle,
            _ => MouseButton.Left
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

            await session.CurrentPage.ClickAsync(selector, new PageClickOptions
            {
                Button = mouseButton,
                ClickCount = clickCount,
                Timeout = config.DefaultTimeout
            });

            var result = new
            {
                status = "success",
                selector,
                clicked = true
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (PlaywrightException ex)
        {
            return Fail($"点击失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
