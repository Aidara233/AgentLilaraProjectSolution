using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Core;

namespace Plugin.GitTools.Tools;

[ToolMeta(Group = "git", ContinueLoop = true, CapabilitySummary = "查看 git 仓库状态")]
public class GitStatusTool : GitToolBase
{
    public GitStatusTool(string workspaceDir, GitRunner runner) : base(workspaceDir, runner) { }

    public override string Name => "git_status";
    public override string Description => "查看 git 仓库工作区状态，包含分支信息和已暂存/未暂存/未跟踪文件。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("repo", "仓库路径（相对于 Workspace 目录）", 0),
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public override async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var repo = Get(resolvedInputs, 0).Trim();
        if (string.IsNullOrEmpty(repo))
            return Fail("repo 不能为空");

        var fullPath = ResolveRepoPath(repo);
        if (fullPath == null)
            return Fail("路径不合法：不能访问 Workspace 目录之外");
        if (!Directory.Exists(fullPath))
            return Fail($"目录不存在: {repo}");
        if (!IsGitRepo(fullPath))
            return Fail($"不是 git 仓库: {repo}");

        var result = await Runner.RunAsync(fullPath, "status --short --branch", 10, ct);
        if (!result.Success)
            return Fail($"git status 失败: {result.Error}");

        return Ok($"[{repo}]\n{result.Output.Trim()}");
    }
}
