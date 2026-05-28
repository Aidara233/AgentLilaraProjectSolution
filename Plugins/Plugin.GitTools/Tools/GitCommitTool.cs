using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Core;

namespace Plugin.GitTools.Tools;

[ToolMeta(Group = "git", ContinueLoop = true, CapabilitySummary = "提交 git 更改")]
public class GitCommitTool : GitToolBase
{
    public GitCommitTool(string workspaceDir, GitRunner runner) : base(workspaceDir, runner) { }

    public override string Name => "git_commit";
    public override string Description => "提交 git 更改。可指定 path 做局部提交，否则提交所有已暂存文件。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("repo", "仓库路径（相对于 Workspace 目录）", 0),
        new("message", "提交信息", 1),
        new("path", "（可选）指定文件/目录路径，仅暂存并提交该路径", 2, isRequired: false),
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public override async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var repo = Get(resolvedInputs, 0).Trim();
        var message = Get(resolvedInputs, 1);
        var path = Get(resolvedInputs, 2).Trim();

        if (string.IsNullOrEmpty(repo))
            return Fail("repo 不能为空");
        if (string.IsNullOrEmpty(message.Trim()))
            return Fail("message 不能为空");

        var fullPath = ResolveRepoPath(repo);
        if (fullPath == null)
            return Fail("路径不合法：不能访问 Workspace 目录之外");
        if (!Directory.Exists(fullPath))
            return Fail($"目录不存在: {repo}");
        if (!IsGitRepo(fullPath))
            return Fail($"不是 git 仓库: {repo}");

        if (!string.IsNullOrEmpty(path))
        {
            var addResult = await Runner.RunAsync(fullPath, $"add -- {path}", 10, ct);
            if (!addResult.Success)
                return Fail($"git add 失败: {addResult.Error}");
        }

        var commitArgs = new List<string> { "commit", "-m", message.Trim() };
        var result = await Runner.RunAsync(fullPath, commitArgs, 10, ct);
        if (!result.Success)
            return Fail($"git commit 失败: {result.Error}");

        return Ok($"[{repo}] 提交成功:\n{result.Output.Trim()}");
    }
}
