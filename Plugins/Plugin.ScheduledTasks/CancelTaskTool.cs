using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;

namespace Plugin.ScheduledTasks;

[ToolMeta(Group = null, ContinueLoop = true, ExpressAvailable = true, CapabilitySummary = "取消定时任务")]
public class CancelTaskTool : ITool
{
    private readonly ScheduledTaskStore _store;

    public string Name => "cancel_task";
    public string Description => "取消一个定时任务。task_id 来自 list_tasks 中显示的 ID（完整或前几位均可）。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("task_id", "任务ID（完整或前缀，来自list_tasks）", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(3);

    public CancelTaskTool(ScheduledTaskStore store)
    {
        _store = store;
    }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var id = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        if (string.IsNullOrWhiteSpace(id))
            return Fail("task_id 不能为空");

        if (_store.RemoveTask(id))
            return Ok($"已取消任务 {id}");

        // Check if there are multiple prefix matches
        var tasks = _store.GetActiveTasks();
        var matches = tasks.FindAll(t => t.Id.StartsWith(id));
        if (matches.Count > 1)
        {
            var list = string.Join("\n", matches.ConvertAll(t =>
                $"  [{t.Id[..8]}] {t.Description} ({t.NextFireTime:yyyy-MM-dd HH:mm:ss})"));
            return Fail($"找到多个匹配任务，请指定更完整的ID：\n{list}");
        }

        return Fail($"未找到任务 {id}。使用 list_tasks 查看所有任务。");
    }

    private static Task<ToolResult> Ok(string data) =>
        Task.FromResult(new ToolResult { Status = "success", Data = data });
    private static Task<ToolResult> Fail(string err) =>
        Task.FromResult(new ToolResult { Status = "failed", Error = err });
}
