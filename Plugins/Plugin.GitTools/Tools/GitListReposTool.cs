using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Config;
using Plugin.GitTools.Core;

namespace Plugin.GitTools.Tools;

[ToolMeta(Group = "git", ContinueLoop = true, CapabilitySummary = "列出已注册的本地仓库")]
public class GitListReposTool : GitToolBase
{
    private readonly RepoRegistry _repos;

    public GitListReposTool(string workspaceDir, GitRunner runner, RepoRegistry repos) : base(workspaceDir, runner)
    {
        _repos = repos;
    }

    public override string Name => "git_list_repos";
    public override string Description => "列出所有已注册的本地仓库，包含路径和远端 URL 信息。";
    public override IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
    public override TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public override Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
    {
        var all = _repos.ListAll();
        if (all.Count == 0)
            return Task.FromResult(Ok("暂无已注册的仓库。使用 git_clone 克隆仓库后会自动注册。"));

        var sb = new StringBuilder();
        sb.AppendLine($"共 {all.Count} 个已注册仓库:");
        foreach (var r in all)
        {
            sb.AppendLine($"  [{r.Name}]");
            sb.AppendLine($"    路径: {r.RelativePath}");
            sb.AppendLine($"    远端: {r.RemoteUrl}");
            if (r.GitHubOwner != null)
                sb.AppendLine($"    GitHub: {r.GitHubOwner}/{r.GitHubRepo}");
        }

        return Task.FromResult(Ok(sb.ToString().TrimEnd()));
    }
}
