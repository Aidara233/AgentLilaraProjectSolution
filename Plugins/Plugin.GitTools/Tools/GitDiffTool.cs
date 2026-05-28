using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Core;

namespace Plugin.GitTools.Tools;

[ToolMeta(Group = "git", ContinueLoop = true, CapabilitySummary = "查看 git 差异")]
public class GitDiffTool : GitToolBase
{
    public GitDiffTool(string workspaceDir, GitRunner runner) : base(workspaceDir, runner) { }

    public override string Name => "git_diff";
    public override string Description => "查看 git 差异。target 可以是 commit、分支或文件路径，默认为工作区 vs HEAD。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("repo", "仓库路径（相对于 Workspace 目录）", 0),
        new("target", "（可选）差异目标：commit/分支/文件路径", 1, isRequired: false),
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

        var target = Get(resolvedInputs, 1).Trim();

        var args = "diff";
        if (!string.IsNullOrEmpty(target))
            args += $" {target}";

        var result = await Runner.RunAsync(fullPath, args, 10, ct);
        if (!result.Success)
            return Fail($"git diff 失败: {result.Error}");

        if (string.IsNullOrEmpty(result.Output.Trim()))
            return Ok($"[{repo}] 无差异");

        var output = result.Output.Trim();
        if (output.Length > 8000)
        {
            var statResult = await Runner.RunAsync(fullPath, "diff --stat" + (string.IsNullOrEmpty(target) ? "" : $" {target}"), 10, ct);
            var stat = statResult.Success ? statResult.Output.Trim() : "";
            return Ok($"[{repo}] 差异较大，统计:\n{stat}\n\n--- 部分 diff ---\n{output[..8000]}\n... (diff 已截断)");
        }

        return Ok($"[{repo}] 差异:\n{output}");
    }
}
