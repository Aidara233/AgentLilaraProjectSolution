// AgentCoreProcessor.Tests/PermissionServiceTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Models;
using AgentCoreProcessor.Tool;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 会话类型权限（SessionTypeProfiles.SessionTools）测试：白名单/黑名单语义 + 种子文件可解析 + 各类型的实际裁决。
/// 工具可见性统一由会话类型文件（Storage/SessionTypes/{type}.json）裁决。
/// </summary>
public class PermissionServiceTests
{
    /// <summary>把 PathConfig.StoragePath 指向真实 Storage（种子文件所在地），测试结束还原。</summary>
    private static IDisposable PointToRealStorage()
    {
        var storage = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Storage"));
        if (!Directory.Exists(Path.Combine(storage, "SessionTypes")))
            throw new InvalidOperationException($"测试找不到 Storage/SessionTypes: {storage}");

        var prop = typeof(PathConfig).GetProperty("StoragePath",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var old = (string)prop!.GetValue(null)!;
        prop.SetValue(null, storage);
        SessionTypeProfiles.Invalidate();

        return new Disposable(() =>
        {
            prop.SetValue(null, old);
            SessionTypeProfiles.Invalidate();
        });
    }

    private sealed class Disposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    [Fact]
    public void SessionTools_Whitelist_OnlyListedAllowed()
    {
        var tools = new SessionTools { Mode = "whitelist", List = ["a", "b"] };
        Assert.True(tools.IsAllowed("a"));
        Assert.False(tools.IsAllowed("c"));
    }

    [Fact]
    public void SessionTools_Blacklist_OnlyListedDenied()
    {
        var tools = new SessionTools { Mode = "blacklist", List = ["x"] };
        Assert.False(tools.IsAllowed("x"));
        Assert.True(tools.IsAllowed("y"));
    }

    [Fact]
    public void SessionTools_IsAllowed_CaseInsensitive()
    {
        var tools = new SessionTools { Mode = "whitelist", List = ["Speak"] };
        Assert.True(tools.IsAllowed("speak"));
        Assert.True(tools.IsAllowed("SPEAK"));
    }

    [Fact]
    public void NullOrEmptyType_IsAllowed_PassesThrough()
    {
        using var _ = PointToRealStorage();
        Assert.True(SessionTypeProfiles.IsAllowed(null, "anything"));
        Assert.True(SessionTypeProfiles.IsAllowed("", "anything"));
    }

    [Fact]
    public void UnknownType_IsAllowed_Throws()
    {
        using var _ = PointToRealStorage();
        // 会话类型即权限作用域：未知类型不在配置库中 → 抛异常（不再静默放行）
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionTypeProfiles.IsAllowed("nonexistent-type", "anything"));
    }

    [Fact]
    public void SeedFiles_Parse_WithExpectedMode()
    {
        using var _ = PointToRealStorage();

        // express：白名单，轻量对话工具 + 心智只读（journal/remember/mind_status），不含工作工具
        Assert.Equal("whitelist", SessionTypeProfiles.GetMode("express"));
        Assert.True(SessionTypeProfiles.IsAllowed("express", "speak"));
        Assert.True(SessionTypeProfiles.IsAllowed("express", "escalate"));
        Assert.True(SessionTypeProfiles.IsAllowed("express", "remember"));
        Assert.False(SessionTypeProfiles.IsAllowed("express", "continue_loop"));
        Assert.False(SessionTypeProfiles.IsAllowed("express", "end_session"));

        // channel：黑名单，escalate + review 工具被禁，其余（含全局工具）放行
        Assert.Equal("blacklist", SessionTypeProfiles.GetMode("channel"));
        Assert.False(SessionTypeProfiles.IsAllowed("channel", "escalate"));
        Assert.False(SessionTypeProfiles.IsAllowed("channel", "review_list"));
        Assert.False(SessionTypeProfiles.IsAllowed("channel", "review_resolve"));
        Assert.True(SessionTypeProfiles.IsAllowed("channel", "speak"));
        Assert.True(SessionTypeProfiles.IsAllowed("channel", "send_email"));
        Assert.True(SessionTypeProfiles.IsAllowed("channel", "end_session"));

        // task：白名单，含核心 session 工具 + 网络工具（http_request/download_file 已开放），不含全局工具
        Assert.Equal("whitelist", SessionTypeProfiles.GetMode("task"));
        Assert.True(SessionTypeProfiles.IsAllowed("task", "report_to_parent"));
        Assert.True(SessionTypeProfiles.IsAllowed("task", "compact"));
        Assert.True(SessionTypeProfiles.IsAllowed("task", "end_session"));
        Assert.True(SessionTypeProfiles.IsAllowed("task", "http_request"));
        Assert.True(SessionTypeProfiles.IsAllowed("task", "download_file"));
        Assert.False(SessionTypeProfiles.IsAllowed("task", "escalate"));
        Assert.False(SessionTypeProfiles.IsAllowed("task", "send_email"));

        // review：白名单，review 工具 + 心智工具 + 会话生命周期，不含文件/网络等全局工具
        Assert.Equal("whitelist", SessionTypeProfiles.GetMode("review"));
        Assert.True(SessionTypeProfiles.IsAllowed("review", "review_list"));
        Assert.True(SessionTypeProfiles.IsAllowed("review", "remember"));
        Assert.True(SessionTypeProfiles.IsAllowed("review", "end_session"));
        Assert.False(SessionTypeProfiles.IsAllowed("review", "send_email"));

        // express：默认 Core 配置从 express.json 读取（ExpressCore）
        Assert.Equal("ExpressCore", SessionTypeProfiles.Get("express").DefaultCoreConfig);
    }

    [Fact]
    public async Task ToolExecutor_DeniesOutOfGroupTool()
    {
        using var _ = PointToRealStorage();
        var executor = ToolExecutorFactory.Create(name => new StubTool(name));
        executor.PermissionGroup = "express";
        var results = await executor.ExecuteAsync(new List<ToolCall> { new() { Tool = "end_session" } });
        Assert.Equal("failed", results[0].Status);
        Assert.Contains("会话类型", results[0].Error);
    }

    [Fact]
    public async Task ToolExecutor_AllowsInGroupTool()
    {
        using var _ = PointToRealStorage();
        var executor = ToolExecutorFactory.Create(name => new StubTool(name));
        executor.PermissionGroup = "express";
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
