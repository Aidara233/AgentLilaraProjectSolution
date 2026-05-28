using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Core;

namespace Plugin.GitTools.Tools;

[ToolMeta(Group = "git", ContinueLoop = true, CapabilitySummary = "查看 git 提交历史")]
public class GitLogTool : GitToolBase
{
    public GitLogTool(string workspaceDir, GitRunner runner) : base(workspaceDir, runner) { }

    public override string Name => "git_log";
    public override string Description => "查看 git 提交历史。支持指定条数和分支。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("repo", "仓库路径（相对于 Workspace 目录）", 0),
        new("count", "（可选）显示条数，默认10", 1, isRequired: false),
        new("branch", "（可选）指定分支，默认当前分支", 2, isRequired: false),
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

        var countStr = Get(resolvedInputs, 1).Trim();
        var branch = Get(resolvedInputs, 2).Trim();

        int count = 10;
        if (!string.IsNullOrEmpty(countStr) && !int.TryParse(countStr, out count))
            return Fail("count 参数无效，需要是数字");

        count = Math.Max(1, Math.Min(count, 50));

        var args = $"log --oneline --decorate -{count}";
        if (!string.IsNullOrEmpty(branch))
            args += $" {branch}";

        var result = await Runner.RunAsync(fullPath, args, 10, ct);
        if (!result.Success)
            return Fail($"git log 失败: {result.Error}");

        return Ok($"[{repo}] 最近 {count} 条提交:\n{result.Output.Trim()}");
    }
}
