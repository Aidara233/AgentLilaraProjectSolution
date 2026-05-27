// Plugins/Plugin.SshTools/SshLoopComponent.cs
using System.Text;
using AgentLilara.PluginSDK;

namespace Plugin.SshTools;

[Component(Name = "ssh-tools", Scope = ComponentScope.Loop)]
[LoopApplicability(Channel = Applicability.Enabled, System = Applicability.Enabled,
    Review = Applicability.Disabled, SubAgent = Applicability.Enabled)]
public class SshLoopComponent : LoopComponentBase
{
    private string _loopId = "";
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
        _loopId = context.LoopId;

        var global = SshToolsAccessor.Global
            ?? throw new InvalidOperationException("SshGlobalComponent 未初始化");

        var workspaceDir = context.Storage.WorkspaceDirectory;
        _workDir = $"/tmp/agent-lilara/{CleanLoopId(_loopId)}";

        // 确保远端工作目录存在
        EnsureRemoteWorkDir(global, _workDir);

        _exec = new ExecTool(global, _workDir, _loopId);
        _upload = new UploadTool(global, _workDir, workspaceDir, _loopId);
        _download = new DownloadTool(global, _workDir, workspaceDir, _loopId);
        _check = new CheckTaskTool(global);
        _kill = new KillTaskTool(global);

        return Task.CompletedTask;
    }

    public override Task OnBeforeInvokeAsync()
    {
        var global = SshToolsAccessor.Global;
        if (global != null)
        {
            var completed = global.DrainCompletedTasks(_loopId);
            _pendingNotifications.AddRange(completed);
        }
        return Task.CompletedTask;
    }

    public override string? BuildPromptSection()
    {
        var global = SshToolsAccessor.Global;
        var sb = new StringBuilder();

        // 连接状态
        var connStatus = global?.Client?.IsConnected == true ? "已连接" : "未连接";
        sb.AppendLine($"[SSH] {global?.Config.Host}:{global?.Config.Port} ({connStatus})");
        sb.AppendLine($"远端工作目录: {_workDir}");
        sb.AppendLine("提示: ssh_exec 同步输出截断 " + ExecTool.MaxOutputChars + " 字符，"
            + "需完整输出请用 `command > file` 重定向后 ssh_download。"
            + "timeout=0 异步执行，超时自动降级，输出写入远端文件，任务完成自动唤醒通知。");

        // 异步任务完成通知
        if (_pendingNotifications.Count > 0)
        {
            sb.AppendLine();
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
        }

        return sb.ToString().TrimEnd();
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

    private static string CleanLoopId(string loopId)
    {
        var sb = new StringBuilder();
        foreach (var c in loopId)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.ToString();
    }
}
