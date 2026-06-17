using System.Text.Json;
using AgentLilara.PluginSDK;
using Microsoft.Playwright;

namespace Plugin.NetworkTools;

[ToolMeta(Group = "browser", ContinueLoop = true)]
public class BrowserScreenshotTool : ITool
{
    private readonly BrowserComponent _component;
    private readonly SecurityConfig _security;
    private readonly string _loopId;

    public string Name => "browser_screenshot";
    public string Description => "截取页面或元素截图。保存路径相对于Workspace目录。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("save_path", "保存路径（相对于Workspace，如screenshots/page.png）", 0),
        new("full_page", "是否截取完整页面：true/false，默认false（可选）", 1, isRequired: false),
        new("selector", "截取特定元素（可选）", 2, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);

    public BrowserScreenshotTool(BrowserComponent component, SecurityConfig security, string loopId)
    {
        _component = component;
        _security = security;
        _loopId = loopId;
    }

    public async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var savePath = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        var fullPageStr = resolvedInputs.Count > 1 ? resolvedInputs[1].Trim().ToLower() : "false";
        var selector = resolvedInputs.Count > 2 ? resolvedInputs[2].Trim() : "";

        if (string.IsNullOrEmpty(savePath))
            return Fail("save_path 不能为空");

        var fullPage = fullPageStr == "true";

        try
        {
            var sessionManager = _component.GetSessionManager();
            var config = _component.GetConfig();
            var storage = _component.GetStorage();

            if (sessionManager == null || config == null || storage == null)
                return Fail("浏览器组件未初始化");

            var session = await sessionManager.GetOrCreateSessionAsync(_loopId);

            if (session.CurrentPage == null)
                return Fail("当前无活动页面，请先使用 browser_navigate");

            // 解析完整路径
            var workspaceDir = storage.WorkspaceDirectory;
            var fullPath = Path.Combine(workspaceDir, savePath);

            // 路径安全校验（必须在 Workspace 内）
            var normalizedPath = Path.GetFullPath(fullPath);
            var normalizedWorkspace = Path.GetFullPath(workspaceDir);
            if (!normalizedPath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
                return Fail($"路径必须在 Workspace 目录内: {workspaceDir}");

            // 扩展名验证
            var ext = Path.GetExtension(savePath).ToLower();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
                return Fail("文件扩展名必须为 .png/.jpg/.jpeg");

            // 创建父目录
            var parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            // 截图
            if (!string.IsNullOrEmpty(selector))
            {
                // 截取特定元素
                var element = await session.CurrentPage.QuerySelectorAsync(selector);
                if (element == null)
                    return Fail($"未找到匹配的元素: {selector}");

                await element.ScreenshotAsync(new ElementHandleScreenshotOptions
                {
                    Path = fullPath
                });
            }
            else
            {
                // 截取整个页面
                await session.CurrentPage.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = fullPath,
                    FullPage = fullPage
                });
            }

            var fileInfo = new FileInfo(fullPath);

            var result = new
            {
                status = "success",
                file_path = savePath,
                size_bytes = fileInfo.Length
            };

            return Ok(JsonSerializer.Serialize(result));
        }
        catch (PlaywrightException ex)
        {
            return Fail($"截图失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail($"文件操作失败: {ex.Message}");
        }
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
