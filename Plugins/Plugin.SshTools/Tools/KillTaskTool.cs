// Plugins/Plugin.SshTools/Tools/KillTaskTool.cs
using AgentLilara.PluginSDK;

namespace Plugin.SshTools;

[ToolMeta(Group = "ssh", ContinueLoop = true, CapabilitySummary = "杀掉异步SSH任务")]
public class KillTaskTool : ITool
{
    private readonly SshGlobalComponent _global;

    public string Name => "ssh_kill";
    public string Description => "杀掉指定的异步 SSH 任务。远端进程会收到 SIGTERM。";
    public IReadOnlyList<ToolParameter> Parameters =>
    [
        new("task_id", "要杀的任务ID", 0)
    ];
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public KillTaskTool(SshGlobalComponent global) { _global = global; }

    public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var taskId = resolvedInputs.Count > 0 ? resolvedInputs[0].Trim() : "";
        if (string.IsNullOrEmpty(taskId))
            return Task.FromResult(Fail("task_id 不能为空"));

        if (!_global.TryGetTask(taskId, out var task) || task == null)
            return Task.FromResult(Fail($"任务不存在: {taskId}"));

        task.Cts?.Cancel();
        _global.CompleteTask(taskId, null, null, null, SshTaskStatus.Killed, "由 ssh_kill 终止");
        return Task.FromResult(Ok($"{{\"task_id\":\"{taskId}\",\"status\":\"killed\"}}"));
    }

    private static ToolResult Fail(string err) => new() { Status = "failed", Error = err };
    private static ToolResult Ok(string data) => new() { Status = "success", Data = data };
}
