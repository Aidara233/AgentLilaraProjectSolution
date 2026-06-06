using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.ScheduledTasks;

[ToolMeta(Group = null, ContinueLoop = true, CapabilitySummary = "列出所有活跃的定时任务")]
public class ListTasksTool : ITool
{
    private readonly ScheduledTaskStore _store;

    public string Name => "list_tasks";
    public string Description => "列出当前频道所有活跃的定时任务。";
    public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public TimeSpan Timeout => TimeSpan.FromSeconds(3);

    public ListTasksTool(ScheduledTaskStore store)
    {
        _store = store;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var tasks = _store.GetActiveTasks()
            .OrderBy(t => t.NextFireTime ?? DateTime.MaxValue)
            .ToList();

        if (tasks.Count == 0)
            return Ok("(没有定时任务)");

        var lines = new List<string> { $"共 {tasks.Count} 个定时任务：" };
        foreach (var t in tasks)
        {
            var fireStr = t.NextFireTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "已完成";
            var recurring = t.IsRecurring ? " [循环]" : "";
            lines.Add($"[{t.Id[..8]}] {t.Description}");
            lines.Add($"  触发: {fireStr}{recurring}  |  表达式: {t.Expression}");
        }

        return Ok(string.Join("\n", lines));
    }

    private static Task<ToolResult> Ok(string data) =>
        Task.FromResult(new ToolResult { Status = "success", Data = data });
}
