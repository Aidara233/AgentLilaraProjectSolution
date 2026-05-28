using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Core;

namespace Plugin.GitTools.Tools;

[ToolMeta(Group = "git", ContinueLoop = true, CapabilitySummary = "从远端拉取 git 更改")]
public class GitPullTool : GitToolBase
{
    public GitPullTool(string workspaceDir, GitRunner runner) : base(workspaceDir, runner) { }

    public override string Name => "git_pull";
    public override string Description => "从远端拉取并合并 git 更改。默认从 origin 的当前分支拉取。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("repo", "仓库路径（相对于 Workspace 目录）", 0),
        new("remote", "（可选）远端名称，默认 origin", 1, isRequired: false),
        new("branch", "（可选）分支名，默认当前分支", 2, isRequired: false),
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(30);

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

        var remote = Get(resolvedInputs, 1).Trim();
        var branch = Get(resolvedInputs, 2).Trim();

        var args = "pull";
        if (!string.IsNullOrEmpty(remote))
            args += $" {remote}";
        if (!string.IsNullOrEmpty(branch))
            args += $" {branch}";

        var result = await Runner.RunAsync(fullPath, args, 30, ct);
        var output = (result.Output + "\n" + result.Error).Trim();

        if (!result.Success)
        {
            if (output.Contains("conflict") || output.Contains("CONFLICT"))
                return Fail($"git pull 存在冲突:\n{output}");
            return Fail($"git pull 失败: {output}");
        }

        return Ok($"[{repo}] pull 成功:\n{output}");
    }
}
