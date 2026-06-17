using System.Text.Json;
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "browser", ContinueLoop = true)]
public class BrowserExecuteJsTool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public string Name => "browser_execute_js";
    public string Description => "在页面上下文中执行JavaScript代码。返回执行结果。超时限制由配置控制。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("script", "JavaScript代码", 0),
        new("args", "传递给脚本的参数（JSON数组，可选）", 1, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public BrowserExecuteJsTool(BrowserComponent component, SecurityConfig security, string loopId)
    {
        _component = component;
        _security = security;
        _loopId = loopId;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var script = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var argsJson = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim() : "";

        if (string.IsNullOrEmpty(script))
            return Fail("script 不能为空");

        try
        {
            var sessionManager = _component.GetSessionManager();
            var config = _component.GetConfig();

            if (sessionManager == null || config == null)
                return Fail("浏览器组件未初始化");

            var session = await sessionManager.GetOrCreateSessionAsync(_loopId);

            if (session.CurrentPage == null)
                return Fail("当前无活动页面，请先使用 browser_navigate");

            // 执行 JavaScript
            var executeResult = await session.CurrentPage.EvaluateAsync<object>(script);

            // 序列化结果
            var resultJson = JsonSerializer.Serialize(executeResult);

            // 检查结果大小限制
            if (resultJson.Length > _security.MaxResponseBodyBytes)
            {
                resultJson = resultJson.Substring(0, _security.MaxResponseBodyBytes) + "...[截断]";
            }

            var result = new
            {
                status = "success",
                result = resultJson
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (PlaywrightException ex)
        {
            return Fail($"JavaScript执行失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail($"执行失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
