// AgentCoreProcessor.Tests/SessionTypeProfilesTests.cs
using System;
using System.IO;
using System.Reflection;
using AgentCoreProcessor.Component;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 会话类型档案（Storage/Session/*.json）与组件配置（Storage/Engine/ComponentConfig.json）测试。
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
        ComponentConfig.Invalidate();
        return new Disposable(() =>
        {
            prop.SetValue(null, old);
            SessionTypeProfiles.Invalidate();
            ComponentConfig.Invalidate();
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
        var profile = SessionTypeProfiles.Get(SessionType.Channel);
        Assert.Contains("system-ops", profile.DefaultComponentIds);
        Assert.DoesNotContain("system-tools", profile.DefaultComponentIds);
        Assert.Equal("channel", profile.PermissionGroup);
        Assert.Equal("blacklist", profile.PermissionMode);
    }

    [Fact]
    public void TaskProfile_IsMinimalWhitelist()
    {
        using var _ = PointToRealStorage();
        var profile = SessionTypeProfiles.Get(SessionType.Task);
        Assert.Equal("task", profile.PermissionGroup);
        Assert.Equal("whitelist", profile.PermissionMode);
        Assert.Contains("report_to_parent", profile.PermissionTools);
        Assert.Contains("compact", profile.PermissionTools);
        Assert.DoesNotContain("send_email", profile.PermissionTools);
        Assert.DoesNotContain("escalate", profile.PermissionTools);
    }

    [Fact]
    public void ComponentConfig_SeededByGroup()
    {
        using var _ = PointToRealStorage();
        var cfg = ComponentConfig.Load();

        // 组件归属按组：system-ops 仅 channel，ssh-tools 仅 task，review-tools 仅 review
        Assert.True(cfg.IsEnabled("system-ops", "channel"));
        Assert.False(cfg.IsEnabled("system-ops", "task"));
        Assert.False(cfg.IsEnabled("ssh-tools", "channel"));
        Assert.True(cfg.IsEnabled("ssh-tools", "task"));
        Assert.True(cfg.IsEnabled("review-tools", "review"));
        Assert.False(cfg.IsEnabled("review-tools", "channel"));

        // Working 会话 profile 组件全在 channel 组
        Assert.True(cfg.IsEnabled("basic-tools", "channel"));
        Assert.True(cfg.IsEnabled("working-tools", "channel"));
        Assert.True(cfg.IsEnabled("memory-tools", "channel"));

        // 未配置组件 → 不启用（config-required）
        Assert.False(cfg.IsEnabled("__nonexistent__", "channel"));
    }
}
