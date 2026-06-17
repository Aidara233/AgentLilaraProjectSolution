using System.Text.Json;
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "browser", ContinueLoop = true)]
public class BrowserCloseSessionTool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public string Name => "browser_close_session";
    public string Description => "关闭当前循环的浏览器会话，释放所有相关资源。下次调用将自动创建新会话。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public BrowserCloseSessionTool(BrowserComponent component, SecurityConfig security, string loopId)
    {
        _component = component;
        _security = security;
        _loopId = loopId;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        try
        {
            var sessionManager = _component.GetSessionManager();

            if (sessionManager == null)
                return Fail("浏览器组件未初始化");

            await sessionManager.CloseSessionAsync(_loopId);

            var result = new
            {
                status = "success",
                session_closed = true
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (Exception ex)
        {
            return Fail($"关闭会话失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
