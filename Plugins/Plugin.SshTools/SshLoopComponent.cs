// Plugins/Plugin.SshTools/SshLoopComponent.cs
using System.Text;
using AgentLilara.PluginSDK;

namespace Plugin.SshTools;

[Component(Name = "ssh-tools", Scope = ComponentScope.Loop)]
public class SshLoopComponent : LoopComponentBase
{
    private string _sessionId = "";
    private string _workDir = "";
    private List<SshTask> _pendingNotifications = new();

    private ExecTool? _exec;
    private UploadTool? _upload;
    private DownloadTool? _download;
    private CheckTaskTool? _check;
    private KillTaskTool? _kill;

    public override ComponentMeta Meta => new()
    {
        Name = "ssh-tools",
        Description = "SSH 远程执行和文件传输",
        DefaultEnabled = true,
        PromptPriority = 40
    };

    public override IEnumerable<ITool> Tools
    {
        get
        {
            if (_exec != null) yield return _exec;
            if (_upload != null) yield return _upload;
            if (_download != null) yield return _download;
            if (_check != null) yield return _check;
            if (_kill != null) yield return _kill;
        }
    }

    public override Task OnInitAsync(ILoopComponentContext context, InitReason reason)
    {
        _sessionId = context.SessionId;

        var global = SshToolsAccessor.Global
            ?? throw new InvalidOperationException("SshGlobalComponent 未初始化");

        var workspace = context.Workspace;
        _workDir = $"/tmp/agent-lilara/{CleanSessionId(_sessionId)}";

        // 远端工作目录懒创建：后台异步执行，不在 session 创建关键路径上同步连 SSH
        // （SSH 不可达时不拖住会话；首轮命令若目录未建好，ExecTool 的 cd 由 sshd 处理）
        _ = Task.Run(() => EnsureRemoteWorkDir(global, _workDir));

        _exec = new ExecTool(global, _workDir, _sessionId);
        _upload = new UploadTool(global, _workDir, workspace, _sessionId);
        _download = new DownloadTool(global, _workDir, workspace, _sessionId);
        _check = new CheckTaskTool(global);
        _kill = new KillTaskTool(global);

        return Task.CompletedTask;
    }

    public override Task OnBeforeInvokeAsync()
    {
        var global = SshToolsAccessor.Global;
        if (global != null)
        {
            var completed = global.DrainCompletedTasks(_sessionId);
            _pendingNotifications.AddRange(completed);
        }
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        if (_pendingNotifications.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("[SSH 任务完成]");
        foreach (var n in _pendingNotifications)
        {
            if (n.Status == SshTaskStatus.Completed)
            {
                sb.Append($"- {n.TaskId}: \"{n.Command}\" → exit {n.ExitCode ?? 0}");
                if (n.StdoutFile != null)
                    sb.Append($" | stdout: {n.StdoutFile}");
                if (n.StderrFile != null)
                    sb.Append($" | stderr: {n.StderrFile}");
                sb.AppendLine();
            }
            else if (n.Status == SshTaskStatus.Failed)
                sb.AppendLine($"- {n.TaskId}: \"{n.Command}\" → 失败: {n.Error}");
            else if (n.Status == SshTaskStatus.Killed)
                sb.AppendLine($"- {n.TaskId}: \"{n.Command}\" → 已终止");
            else if (n.Status == SshTaskStatus.TimedOut)
                sb.AppendLine($"- {n.TaskId}: \"{n.Command}\" → 超时");
        }
        _pendingNotifications.Clear();

        return sb.ToString().TrimEnd();
    }

    public override Task<bool> OnCanExitAsync()
    {
        var global = SshToolsAccessor.Global;
        if (global == null) return Task.FromResult(true);
        // _sessionId 为裸 sessionId（LoopId 前缀概念已移除）
        return Task.FromResult(!global.HasActiveResources(_sessionId));
    }

    private static void EnsureRemoteWorkDir(SshGlobalComponent global, string workDir)
    {
        var client = global.EnsureConnected();
        if (client == null) return;

        try
        {
            var escaped = "'" + workDir.Replace("'", "'\\''") + "'";
            using var cmd = client.CreateCommand($"mkdir -p {escaped}");
            cmd.Execute();
        }
        catch { /* 首轮失败不阻塞 */ }
    }

    private static string CleanSessionId(string sessionId)
    {
        var sb = new StringBuilder();
        foreach (var c in sessionId)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }
}
