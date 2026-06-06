using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.ScheduledTasks;

[ToolMeta(Group = null, ContinueLoop = true, CapabilitySummary = "确认已处理定时任务通知")]
public class DismissNotificationTool : ITool
{
    private readonly Action _clearNotification;

    public string Name => "dismiss_notification";
    public string Description => "确认已处理所有定时任务通知，清除当前通知提示。"
        + "应在处理完通知中列出的所有任务后调用，之后通知不再显示。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(2);

    public DismissNotificationTool(Action clearNotification)
    {
        _clearNotification = clearNotification;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        _clearNotification();
        return Task.FromResult(new ToolResult { Status = "success", Data = "通知已清除" });
    }
}
