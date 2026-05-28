using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Core;

namespace Plugin.GitTools.Tools;

[ToolMeta(Group = "git", ContinueLoop = true, CapabilitySummary = "管理 git 分支")]
public class GitBranchTool : GitToolBase
{
    public GitBranchTool(string workspaceDir, GitRunner runner) : base(workspaceDir, runner) { }

    public override string Name => "git_branch";
    public override string Description => "管理 git 分支。action: list(列出所有分支) / create(创建新分支) / switch(切换分支) / delete(删除分支)。";
    public override IReadOnlyList<ToolParameter> Parameters =>
    [
        new("repo", "仓库路径（相对于 Workspace 目录）", 0),
        new("action", "操作类型: list / create / switch / delete", 1),
        new("name", "（可选）分支名（create/switch/delete 需要）", 2, isRequired: false),
    ];
    public override TimeSpan Timeout => TimeSpan.FromSeconds(10);

    public override async Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var repo = Get(resolvedInputs, 0).Trim();
        var action = Get(resolvedInputs, 1).Trim().ToLowerInvariant();
        var name = Get(resolvedInputs, 2).Trim();

        if (string.IsNullOrEmpty(repo))
            return Fail("repo 不能为空");
        if (string.IsNullOrEmpty(action))
            return Fail("action 不能为空，可选: list / create / switch / delete");

        var fullPath = ResolveRepoPath(repo);
        if (fullPath == null)
            return Fail("路径不合法：不能访问 Workspace 目录之外");
        if (!Directory.Exists(fullPath))
            return Fail($"目录不存在: {repo}");
        if (!IsGitRepo(fullPath))
            return Fail($"不是 git 仓库: {repo}");

        var result = action switch
        {
            "list" => await DoList(fullPath, repo, ct),
            "create" => await DoCreate(fullPath, repo, name, ct),
            "switch" => await DoSwitch(fullPath, repo, name, ct),
            "delete" => await DoDelete(fullPath, repo, name, ct),
            _ => Fail($"未知 action: {action}，可选: list / create / switch / delete")
        };

        return result;
    }

    private async Task<ToolResult> DoList(string fullPath, string repo, CancellationToken ct)
    {
        var result = await Runner.RunAsync(fullPath, "branch -a -v", 10, ct);
        if (!result.Success)
            return Fail($"git branch 失败: {result.Error}");
        return Ok($"[{repo}] 分支列表:\n{result.Output.Trim()}");
    }

    private async Task<ToolResult> DoCreate(string fullPath, string repo, string name, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(name))
            return Fail("create 需要指定分支名 (name 参数)");
        var result = await Runner.RunAsync(fullPath, $"branch {name}", 10, ct);
        if (!result.Success)
            return Fail($"创建分支失败: {result.Error}");
        return Ok($"[{repo}] 已创建分支: {name}");
    }

    private async Task<ToolResult> DoSwitch(string fullPath, string repo, string name, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(name))
            return Fail("switch 需要指定分支名 (name 参数)");
        var result = await Runner.RunAsync(fullPath, $"checkout {name}", 10, ct);
        var output = (result.Output + "\n" + result.Error).Trim();
        if (!result.Success)
            return Fail($"切换分支失败: {output}");
        return Ok($"[{repo}] 已切换到分支: {name}\n{output}");
    }

    private async Task<ToolResult> DoDelete(string fullPath, string repo, string name, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(name))
            return Fail("delete 需要指定分支名 (name 参数)");
        var result = await Runner.RunAsync(fullPath, $"branch -d {name}", 10, ct);
        if (!result.Success)
        {
            var forceResult = await Runner.RunAsync(fullPath, $"branch -D {name}", 10, ct);
            if (!forceResult.Success)
                return Fail($"删除分支失败: {result.Error}");
            return Ok($"[{repo}] 已强制删除分支: {name}");
        }
        return Ok($"[{repo}] 已删除分支: {name}");
    }
}
