using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Config;
using Plugin.GitTools.Core;

namespace Plugin.GitTools.Tools;

[ToolMeta(Group = "git", ContinueLoop = true, CapabilitySummary = "初始化一个新的 git 仓库")]
public class GitInitTool : GitToolBase
{
    private readonly RepoRegistry _repos;

    public GitInitTool(string workspaceDir, GitRunner runner, RepoRegistry repos) : base(workspaceDir, runner)
    {
        _repos = repos;
    }

    public override string Name => "git_init";
    public override string Description => "在 Workspace 目录下初始化一个新的 git 仓库。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("path", "目录名（相对于 Workspace 目录）", 0),
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public override async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var path = Get(resolvedInputs, 0).Trim();
        if (string.IsNullOrEmpty(path))
            return Fail("path 不能为空");

        var fullPath = ResolveRepoPath(path);
        if (fullPath == null)
            return Fail("路径不合法：不能访问 Workspace 目录之外");

        if (Directory.Exists(fullPath) && IsGitRepo(fullPath))
            return Fail($"目录已是 git 仓库: {path}");

        Directory.CreateDirectory(fullPath);

        var result = await Runner.RunAsync(fullPath, "init", 10, ct);
        if (!result.Success)
            return Fail($"git init 失败: {result.Error}");

        var entry = new RepoEntry
        {
            Name = path,
            RelativePath = path,
            RemoteUrl = "",
            RegisteredAt = DateTime.Now
        };
        _repos.Register(entry);

        return Ok($"[{path}] 已初始化 git 仓库\n{result.Output.Trim()}\n\n提示：使用 git_commit 提交更改，添加 remote 后可用 git_push 推送。");
    }
}
