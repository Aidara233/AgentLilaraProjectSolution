// Plugins/Plugin.NetworkTools/BrowserToolsLoopComponent.cs
using AgentLilara.PluginSDK;

namespace Plugin.NetworkTools;

[Component(Name = "browser-tools", Scope = ComponentScope.Loop)]
public class BrowserToolsLoopComponent : LoopComponentBase
{
    private string _sessionId = "";

    private BrowserNavigateTool? _navigate;
    private BrowserClickTool? _click;
    private BrowserFillTool? _fill;
    private BrowserExtractTool? _extract;
    private BrowserScreenshotTool? _screenshot;
    private BrowserExecuteJsTool? _executeJs;
    private BrowserGetCookiesTool? _getCookies;
    private BrowserSetCookiesTool? _setCookies;
    private BrowserWaitForTool? _waitFor;
    private BrowserCloseSessionTool? _closeSession;

    public override ComponentMeta Meta => new()
    {
        Name = "browser-tools",
        Description = "Playwright 浏览器自动化：导航/点击/填表/提取/截图/JS/Cookie/等待/关闭会话",
        DefaultEnabled = true,
        PromptPriority = 50
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_navigate != null) yield return _navigate;
            if (_click != null) yield return _click;
            if (_fill != null) yield return _fill;
            if (_extract != null) yield return _extract;
            if (_screenshot != null) yield return _screenshot;
            if (_executeJs != null) yield return _executeJs;
            if (_getCookies != null) yield return _getCookies;
            if (_setCookies != null) yield return _setCookies;
            if (_waitFor != null) yield return _waitFor;
            if (_closeSession != null) yield return _closeSession;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _sessionId = context.SessionId;

        var browser = BrowserAccessor.Instance
            ?? throw new InvalidOperationException("BrowserComponent 未初始化");
        var security = NetworkToolsAccessor.Security
            ?? throw new InvalidOperationException("SecurityConfig 未加载（NetworkToolsGlobalComponent 未初始化）");

        _navigate = new BrowserNavigateTool(browser, security, _sessionId);
        _click = new BrowserClickTool(browser, security, _sessionId);
        _fill = new BrowserFillTool(browser, security, _sessionId);
        _extract = new BrowserExtractTool(browser, security, _sessionId);
        _screenshot = new BrowserScreenshotTool(browser, security, _sessionId);
        _executeJs = new BrowserExecuteJsTool(browser, security, _sessionId);
        _getCookies = new BrowserGetCookiesTool(browser, security, _sessionId);
        _setCookies = new BrowserSetCookiesTool(browser, security, _sessionId);
        _waitFor = new BrowserWaitForTool(browser, security, _sessionId);
        _closeSession = new BrowserCloseSessionTool(browser, security, _sessionId);

        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        return null;
    }
}
