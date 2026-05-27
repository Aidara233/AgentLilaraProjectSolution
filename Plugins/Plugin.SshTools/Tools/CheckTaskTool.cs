// Plugins/Plugin.SshTools/Tools/CheckTaskTool.cs
using System.Text.Json;
using AgentLilara.PluginSDK;

namespace Plugin.SshTools;

[ToolMeta(Group = "ssh", ContinueLoop = true, CapabilitySummary = "查询异步SSH任务状态")]
public class CheckTaskTool : ITool
{
    private readonly SshGlobalComponent _global;

    public string Name => "ssh_check";
    public string Description => "查询 SSH 异步任务状态。不传 task_id 列出所有进行中/已完成的任务。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("task_id", "任务ID，不传列出全部", 0, isRequired: false)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public CheckTaskTool(SshGlobalComponent global) { _global = global; }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var taskId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";

        if (!string.IsNullOrEmpty(taskId))
        {
            if (_global.TryGetTask(taskId, out var task) && task != null)
                return Task.FromResult(Ok(FormatTask(task)));
            return Task.FromResult(Fail($"任务不存在: {taskId}"));
        }

        var running = _global.GetAllRunningTasks();
        var items = running.Select(FormatTask).ToList();
        return Task.FromResult(Ok(JsonSerializer.Serialize(items)));
    }

    private static string FormatTask(SshTask t)
    {
        var result = new Dictionary<string, object>
        {
            ["task_id"] = t.TaskId,
            ["command"] = t.Command,
            ["status"] = t.Status.ToString().ToLower(),
            ["started_at"] = t.StartedAt.ToString("o")
        };
        if (t.ExitCode.HasValue) result["exit_code"] = t.ExitCode.Value;
        if (t.Stdout != null) result["stdout"] = Truncate(t.Stdout, 2000);
        if (t.Stderr != null) result["stderr"] = Truncate(t.Stderr, 500);
        if (t.StdoutFile != null) result["stdout_file"] = t.StdoutFile;
        if (t.StderrFile != null) result["stderr_file"] = t.StderrFile;
        if (t.Error != null) result["error"] = t.Error;
        if (t.CompletedAt.HasValue) result["completed_at"] = t.CompletedAt.Value.ToString("o");
        return JsonSerializer.Serialize(result);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
