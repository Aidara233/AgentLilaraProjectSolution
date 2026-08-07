// AgentCoreProcessor.Tests/SessionTypeProfilesTests.cs
using System;
using System.IO;
using System.Reflection;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine.Session;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 会话类型档案（Storage/SessionTypes/*.json）测试：组件清单 + 工具白/黑名单 + Core 选择，全部自包含于类型文件。
/// </summary>
public class SessionTypeProfilesTests
{
    private static IDisposable PointToRealStorage()
    {
        var storage = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Storage"));
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
    public void ChannelProfile_UsesSystemOps_NotSystemTools()
    {
        using var _ = PointToRealStorage();
        var profile = SessionTypeProfiles.Get("channel");
        Assert.Contains("system-ops", profile.Components);
        Assert.DoesNotContain("system-tools", profile.Components);
        Assert.Equal("channel", profile.Type);
        Assert.Equal("blacklist", profile.Tools.Mode);
    }

    [Fact]
    public void TaskProfile_IsMinimalWhitelist()
    {
        using var _ = PointToRealStorage();
        var profile = SessionTypeProfiles.Get("task");
        Assert.Equal("task", profile.Type);
        Assert.Equal("whitelist", profile.Tools.Mode);
        Assert.Contains("report_to_parent", profile.Tools.List);
        Assert.Contains("compact", profile.Tools.List);
        Assert.DoesNotContain("send_email", profile.Tools.List);
        Assert.DoesNotContain("escalate", profile.Tools.List);
    }

    [Fact]
    public void ChannelProfile_AllowedCores_ContainsChannelAndExpress()
    {
        using var _ = PointToRealStorage();
        var profile = SessionTypeProfiles.Get("channel");
        Assert.Contains("ChannelCore", profile.AllowedCoreConfigs);
        Assert.Contains("ExpressCore", profile.AllowedCoreConfigs);
        Assert.Contains(profile.DefaultCoreConfig, profile.AllowedCoreConfigs);
    }

    [Fact]
    public void TaskProfile_AllowedCores_FallsBackToSingleDefault()
    {
        using var _ = PointToRealStorage();
        var profile = SessionTypeProfiles.Get("task");
        // task.json 显式单核；即便缺省也应回退到默认核
        Assert.Equal(new[] { "TaskCore" }, profile.AllowedCoreConfigs);
        Assert.Single(profile.AllowedCoreConfigs);
    }

    [Fact]
    public void All_DiscoversConfigLibrary_SessionTypes()
    {
        using var _ = PointToRealStorage();
        var all = SessionTypeProfiles.All();
        // 内置 channel/task/express + review.json 档案自动被发现
        Assert.Contains("channel", all);
        Assert.Contains("task", all);
        Assert.Contains("review", all);
        Assert.Contains("express", all); // express.json 已升级为会话类型（Type 字段）
    }

    [Fact]
    public void ExpressProfile_IsFullSessionType()
    {
        using var _ = PointToRealStorage();
        var profile = SessionTypeProfiles.Get("express");
        Assert.Equal("express", profile.Type);
        Assert.Equal("ExpressCore", profile.DefaultCoreConfig);
        Assert.Equal("whitelist", profile.Tools.Mode);
        Assert.Contains("basic-tools", profile.Components);
        // 白名单工具集
        Assert.Contains("escalate", profile.Tools.List);
        Assert.Contains("speak", profile.Tools.List);
        Assert.Contains("connect_channel", profile.Tools.List); // 初始化连接频道
        // 记忆只读：无写工具
        Assert.DoesNotContain("memory_store", profile.Tools.List);
        Assert.DoesNotContain("memory_update", profile.Tools.List);
    }

    [Fact]
    public void ReviewProfile_LoadsFromConfigLibrary()
    {
        using var _ = PointToRealStorage();
        var profile = SessionTypeProfiles.Get("review");
        Assert.Equal("review", profile.Type);
        Assert.Equal("TaskCore", profile.DefaultCoreConfig); // ReviewCore 已随重设计删除，review 复用 TaskCore
        Assert.Equal("whitelist", profile.Tools.Mode);
        Assert.Contains("review-tools", profile.Components);
    }

    [Fact]
    public void ChannelProfile_PromptFiles_IncludeWorkflow()
    {
        using var _ = PointToRealStorage();
        var profile = SessionTypeProfiles.Get("channel");
        Assert.Contains("identity.txt", profile.PromptFiles);
        Assert.Contains("channel_workflow.txt", profile.PromptFiles);
        Assert.DoesNotContain("express_workflow.txt", profile.PromptFiles);
    }

    [Fact]
    public void TaskProfile_PromptFiles_IncludeSubAgent()
    {
        using var _ = PointToRealStorage();
        var profile = SessionTypeProfiles.Get("task");
        Assert.Contains("task_workflow.txt", profile.PromptFiles);
        Assert.Contains("SubAgentPrompt.txt", profile.PromptFiles);
        Assert.Contains("identity.txt", profile.PromptFiles);
    }

    [Fact]
    public void ReviewProfile_PromptFiles_IncludeReviewPrompt()
    {
        using var _ = PointToRealStorage();
        var profile = SessionTypeProfiles.Get("review");
        Assert.Contains("ReviewPrompt.txt", profile.PromptFiles);
    }

    [Fact]
    public void UnknownType_NotInConfigLibrary_Throws()
    {
        using var _ = PointToRealStorage();
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionTypeProfiles.Get("__nonexistent__"));
    }

    [Fact]
    public void Components_AssembledByType_SingleSource()
    {
        using var _ = PointToRealStorage();

        // channel：工作组件全集（含网络工具，用户 2026-08-07 开放）
        var channel = SessionTypeProfiles.Get("channel");
        Assert.Contains("system-ops", channel.Components);
        Assert.Contains("working-tools", channel.Components);
        Assert.Contains("basic-tools", channel.Components);
        Assert.Contains("network-tools", channel.Components);

        // task：任务组件（含网络工具），不含工作组件
        var task = SessionTypeProfiles.Get("task");
        Assert.Contains("sub-agent-runtime", task.Components);
        Assert.Contains("network-tools", task.Components);
        Assert.DoesNotContain("working-tools", task.Components);
        Assert.DoesNotContain("system-ops", task.Components);

        // review：复盘组件专属
        var review = SessionTypeProfiles.Get("review");
        Assert.Contains("review-tools", review.Components);
        Assert.DoesNotContain("review-tools", channel.Components);
        Assert.DoesNotContain("review-tools", task.Components);
    }
}
