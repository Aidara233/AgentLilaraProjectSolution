using System.Text.Json;
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "browser", ContinueLoop = true)]
public class BrowserFillTool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public string Name => "browser_fill";
    public string Description => "填充表单字段。清空现有内容后输入新值。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("selector", "表单字段选择器", 0),
        new("value", "填充的值", 1)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public BrowserFillTool(BrowserComponent component, SecurityConfig security, string loopId)
    {
        _component = component;
        _security = security;
        _loopId = loopId;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var selector = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var value = resolvedInputs.Count > 1 ? resolvedInputs[1] : "";

        if (string.IsNullOrEmpty(selector))
            return Fail("selector 不能为空");

        try
        {
            var sessionManager = _component.GetSessionManager();
            var config = _component.GetConfig();

            if (sessionManager == null || config == null)
                return Fail("浏览器组件未初始化");

            var session = await sessionManager.GetOrCreateSessionAsync(_loopId);

            if (session.CurrentPage == null)
                return Fail("当前无活动页面，请先使用 browser_navigate");

            await session.CurrentPage.FillAsync(selector, value, new PageFillOptions
            {
                Timeout = config.DefaultTimeout
            });

            var result = new
            {
                status = "success",
                selector,
                filled = true
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (PlaywrightException ex)
        {
            return Fail($"填充失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
