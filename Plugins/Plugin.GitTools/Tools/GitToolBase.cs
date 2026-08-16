using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AgentLilara.PluginSDK;
using Plugin.GitTools.Core;

namespace Plugin.GitTools.Tools;

public abstract class GitToolBase : ITool
{
    protected readonly IWorkspaceAccess Workspace;
    protected readonly GitRunner Runner;

    /// <summary>工作目录绝对路径（仅展示用；路径解析一律走 Workspace.Resolve）。</summary>
    protected string WorkspaceDir => Workspace.Resolve("") ?? "";

    protected GitToolBase(IWorkspaceAccess workspace, GitRunner runner)
    {
        Workspace = workspace;
        Runner = runner;
    }

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<ToolParameter> Parameters { get; }
    public abstract TimeSpan Timeout { get; }
    public abstract Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct);

    protected string? ResolveRepoPath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return null;
        return Workspace.Resolve(relativePath);
    }

    protected bool IsGitRepo(string fullPath)
    {
        return Directory.Exists(Path.Combine(fullPath, ".git"));
    }

    protected static ToolResult Ok(string data) =>
        new() { Status = "success", Data = data };

    protected static ToolResult Fail(string error) =>
        new() { Status = "failed", Error = error };

    protected static string Get(List<string> list, int index) =>
        index < list.Count ? list[index] : "";
}
