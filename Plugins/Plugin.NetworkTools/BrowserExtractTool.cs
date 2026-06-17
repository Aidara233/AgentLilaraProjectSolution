using System.Text.Json;
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "browser", ContinueLoop = true)]
public class BrowserExtractTool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public string Name => "browser_extract_text";
    public string Description => "提取页面文本或HTML。默认提取第一个匹配元素，multiple=true时提取所有（最多100个）。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("selector", "元素选择器，省略则提取整个body（可选）", 0, isRequired: false),
        new("extract_type", "提取类型：text/html/inner_html，默认text（可选）", 1, isRequired: false),
        new("multiple", "是否提取多个匹配元素：true/false，默认false（可选）", 2, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);

    public BrowserExtractTool(BrowserComponent component, SecurityConfig security, string loopId)
    {
        _component = component;
        _security = security;
        _loopId = loopId;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var selector = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "body";
        var extractType = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToLower() : "text";
        var multipleStr = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim().ToLower() : "false";

        if (string.IsNullOrEmpty(selector))
            selector = "body";

        var multiple = multipleStr == "true";

        try
        {
            var sessionManager = _component.GetSessionManager();
            var config = _component.GetConfig();

            if (sessionManager == null || config == null)
                return Fail("浏览器组件未初始化");

            var session = await sessionManager.GetOrCreateSessionAsync(_loopId);

            if (session.CurrentPage == null)
                return Fail("当前无活动页面，请先使用 browser_navigate");

            if (multiple)
            {
                // 提取多个元素
                var elements = await session.CurrentPage.QuerySelectorAllAsync(selector);
                var maxCount = Math.Min(elements.Count, 100);
                var contents = new List<string>();

                for (int i = 0; i < maxCount; i++)
                {
                    var content = await ExtractFromElementAsync(elements[i], extractType);
                    if (content != null)
                        contents.Add(content);
                }

                var result = new
                {
                    status = "success",
                    selector,
                    contents,
                    extract_type = extractType,
                    match_count = contents.Count
                };

                return Ok(JsonSerializer.Serialize(result));
            }
            else
            {
                // 提取单个元素
                var element = await session.CurrentPage.QuerySelectorAsync(selector);
                if (element == null)
                    return Fail($"未找到匹配的元素: {selector}");

                var content = await ExtractFromElementAsync(element, extractType);

                var result = new
                {
                    status = "success",
                    selector,
                    content,
                    extract_type = extractType,
                    match_count = 1
                };

                return Ok(JsonSerializer.Serialize(result));
            }
        }
        catch (PlaywrightException ex)
        {
            return Fail($"提取失败: {ex.Message}");
        }
    }

    private static async Task<string?> ExtractFromElementAsync(IElementHandle element, string extractType)
    {
        return extractType switch
        {
            "html" => await element.EvaluateAsync<string>("el => el.outerHTML"),
            "inner_html" => await element.EvaluateAsync<string>("el => el.innerHTML"),
            _ => await element.TextContentAsync()
        };
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
