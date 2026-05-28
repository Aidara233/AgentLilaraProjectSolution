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
    protected readonly string WorkspaceDir;
    protected readonly GitRunner Runner;

    protected GitToolBase(string workspaceDir, GitRunner runner)
    {
        WorkspaceDir = Path.GetFullPath(workspaceDir);
        Directory.CreateDirectory(WorkspaceDir);
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

        var full = Path.GetFullPath(Path.Combine(WorkspaceDir, relativePath));
        var workspaceRoot = WorkspaceDir.EndsWith(Path.DirectorySeparatorChar)
            ? WorkspaceDir : WorkspaceDir + Path.DirectorySeparatorChar;

        return full.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase)
            || full.Equals(WorkspaceDir, StringComparison.OrdinalIgnoreCase) ? full : null;
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
