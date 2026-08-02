// AgentCoreProcessor.Tests/PermissionServiceTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 权限组（PermissionService）测试：白名单/黑名单语义 + 种子文件可解析 + 各权限组的实际裁决。
/// </summary>
public class PermissionServiceTests
{
    /// <summary>把 PathConfig.StoragePath 指向真实 Storage（种子文件所在地），测试结束还原。</summary>
    private static IDisposable PointToRealStorage()
    {
        var storage = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Storage"));
        if (!Directory.Exists(Path.Combine(storage, "Session")))
            throw new InvalidOperationException($"测试找不到 Storage/Session: {storage}");

        var prop = typeof(PathConfig).GetProperty("StoragePath",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var old = (string)prop!.GetValue(null)!;
        prop.SetValue(null, storage);
        PermissionService.Invalidate();

        return new Disposable(() =>
        {
            prop.SetValue(null, old);
            PermissionService.Invalidate();
        });
    }

    private sealed class Disposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [Fact]
    public void Entry_Whitelist_OnlyListedAllowed()
    {
        var entry = new PermissionGroupEntry { PermissionMode = "whitelist", PermissionTools = ["a", "b"] };
        Assert.True(entry.IsAllowed("a"));
        Assert.False(entry.IsAllowed("c"));
    }

    [Fact]
    public void Entry_Blacklist_OnlyListedDenied()
    {
        var entry = new PermissionGroupEntry { PermissionMode = "blacklist", PermissionTools = ["x"] };
        Assert.False(entry.IsAllowed("x"));
        Assert.True(entry.IsAllowed("y"));
    }

    [Fact]
    public void Entry_IsAllowed_CaseInsensitive()
    {
        var entry = new PermissionGroupEntry { PermissionMode = "whitelist", PermissionTools = ["Speak"] };
        Assert.True(entry.IsAllowed("speak"));
        Assert.True(entry.IsAllowed("SPEAK"));
    }

    [Fact]
    public void UnknownGroup_IsAllowed_ReturnsTrue()
    {
        using var _ = PointToRealStorage();
        Assert.True(PermissionService.IsAllowed("nonexistent-group", "anything"));
    }

    [Fact]
    public void SeedFiles_Parse_WithExpectedMode()
    {
        using var _ = PointToRealStorage();

        // express：白名单，轻量对话工具 + memory 只读，不含 continue_loop/escalate 之外的工作工具
        Assert.Equal("whitelist", PermissionService.GetMode("express"));
        Assert.True(PermissionService.IsAllowed("express", "speak"));
        Assert.True(PermissionService.IsAllowed("express", "escalate"));
        Assert.True(PermissionService.IsAllowed("express", "memory_search"));
        Assert.False(PermissionService.IsAllowed("express", "continue_loop"));
        Assert.False(PermissionService.IsAllowed("express", "end_session"));

        // channel：黑名单，escalate + review 工具被禁，其余（含全局工具）放行
        Assert.Equal("blacklist", PermissionService.GetMode("channel"));
        Assert.False(PermissionService.IsAllowed("channel", "escalate"));
        Assert.False(PermissionService.IsAllowed("channel", "review_browse"));
        Assert.False(PermissionService.IsAllowed("channel", "review_evaluate"));
        Assert.True(PermissionService.IsAllowed("channel", "speak"));
        Assert.True(PermissionService.IsAllowed("channel", "send_email"));
        Assert.True(PermissionService.IsAllowed("channel", "end_session"));

        // task：白名单，含核心 session 工具，不含全局工具
        Assert.Equal("whitelist", PermissionService.GetMode("task"));
        Assert.True(PermissionService.IsAllowed("task", "report_to_parent"));
        Assert.True(PermissionService.IsAllowed("task", "compact"));
        Assert.True(PermissionService.IsAllowed("task", "end_session"));
        Assert.False(PermissionService.IsAllowed("task", "escalate"));
        Assert.False(PermissionService.IsAllowed("task", "send_email"));

        // review：白名单，review 工具 + memory，不含文件/网络等全局工具
        Assert.Equal("whitelist", PermissionService.GetMode("review"));
        Assert.True(PermissionService.IsAllowed("review", "review_browse"));
        Assert.True(PermissionService.IsAllowed("review", "memory_search"));
        Assert.False(PermissionService.IsAllowed("review", "end_session"));
        Assert.False(PermissionService.IsAllowed("review", "send_email"));

        // express：默认 Core 配置从 express.json 读取（ExpressCore）
        Assert.Equal("ExpressCore", PermissionService.GetCoreConfig("express"));
    }

    [Fact]
    public async Task ToolExecutor_DeniesOutOfGroupTool()
    {
        using var _ = PointToRealStorage();
        var executor = new ToolExecutor(name => new StubTool(name)) { PermissionGroup = "express" };
        var results = await executor.ExecuteAsync(new List<ToolCall> { new() { Tool = "end_session" } });
        Assert.Equal("failed", results[0].Status);
        Assert.Contains("权限组", results[0].Error);
    }

    [Fact]
    public async Task ToolExecutor_AllowsInGroupTool()
    {
        using var _ = PointToRealStorage();
        var executor = new ToolExecutor(name => new StubTool(name)) { PermissionGroup = "express" };
        var results = await executor.ExecuteAsync(new List<ToolCall> { new() { Tool = "speak" } });
        Assert.Equal("success", results[0].Status);
    }

    /// <summary>最小 ITool 桩：任何名字都成功执行。</summary>
    private sealed class StubTool(string name) : ITool
    {
        public string Name => name;
        public string Description => "stub";
        public IReadOnlyList<ToolParameter> Parameters => Array.Empty<ToolParameter>();
        public TimeSpan Timeout => TimeSpan.FromSeconds(1);
        public Task<ToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct)
            => Task.FromResult(new ToolResult { Status = "success", Data = "ok" });
    }
}
