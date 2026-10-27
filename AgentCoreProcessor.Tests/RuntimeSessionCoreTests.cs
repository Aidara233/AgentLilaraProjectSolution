// AgentCoreProcessor.Tests/RuntimeSessionCoreTests.cs
using System;
using System.IO;
using System.Reflection;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Engine.Session;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// RuntimeSession 多核支持（ConfigureCores / SwitchCore 白名单校验）测试。
/// 用临时 StoragePath 隔离，避免把测试 session 写到真实存档目录。
/// </summary>
public class RuntimeSessionCoreTests
{
    private static IDisposable PointToTempStorage()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "lilara-core-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        var prop = typeof(PathConfig).GetProperty("StoragePath",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var old = (string)prop!.GetValue(null)!;
        prop.SetValue(null, tmp);
        SessionTypeProfiles.Invalidate();
        return new Disposable(() =>
        {
            prop.SetValue(null, old);
            SessionTypeProfiles.Invalidate();
            try { Directory.Delete(tmp, true); } catch { }
        });
    }

    private sealed class Disposable(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    private static RuntimeSession NewChannelSession()
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        session.SetModelFactory(_ => new FakeAgentModel());
        return session;
    }

    private sealed class FakeAgentModel : AgentCoreProcessor.Core.IAgentModel
    {
        public bool UseNativeTools => true;
        public bool DisableImageMessages => false;
        public string? SessionPrompt { private get; set; }
        public void ConfigureDefaults(string engineType, IReadOnlyList<ITool> tools) { }
        public Task<AgentCoreProcessor.Core.ModelOutput> InvokeAsync(List<AgentCoreProcessor.Models.Message> messages,
            AgentCoreProcessor.Core.AgentInvokeRequest request) => throw new NotSupportedException();
    }

    [Fact]
    public void SwitchCore_ValidatesAgainstWhitelist()
    {
        using var _ = PointToTempStorage();
        var rs = NewChannelSession();
        rs.ConfigureCores(new[] { "ChannelCore", "ExpressCore" }, "ChannelCore");

        Assert.Equal("ChannelCore", rs.ActiveCoreConfig);
        Assert.Equal(2, rs.AllowedCoreConfigs.Count);

        // 白名单内切换成功
        Assert.True(rs.SwitchCore("ExpressCore"));
        Assert.Equal("ExpressCore", rs.ActiveCoreConfig);

        // 白名单外拒绝，保持原核
        Assert.False(rs.SwitchCore("NotACore"));
        Assert.Equal("ExpressCore", rs.ActiveCoreConfig);
    }

    [Fact]
    public void ConfigureCores_EmptyAllowed_FallsBackToSingleDefault()
    {
        using var _ = PointToTempStorage();
        var rs = NewChannelSession();
        rs.ConfigureCores(Array.Empty<string>(), "TaskCore");

        Assert.Single(rs.AllowedCoreConfigs);
        Assert.Equal("TaskCore", rs.ActiveCoreConfig);
        // 单核不注入 switch_core 工具
        Assert.DoesNotContain("switch_core", rs.ActiveTools);
    }

    [Fact]
    public void ConfigureCores_ActiveOverride_Precedence()
    {
        using var _ = PointToTempStorage();
        var rs = NewChannelSession();
        rs.ConfigureCores(new[] { "ChannelCore", "ExpressCore" }, "ChannelCore", activeOverride: "ExpressCore");
        Assert.Equal("ExpressCore", rs.ActiveCoreConfig);

        // 非法 override 回退默认
        rs.ConfigureCores(new[] { "ChannelCore", "ExpressCore" }, "ChannelCore", activeOverride: "Bogus");
        Assert.Equal("ChannelCore", rs.ActiveCoreConfig);
    }

    [Fact]
    public void ConfigureCores_MultiCore_InjectsSwitchCoreTool()
    {
        using var _ = PointToTempStorage();
        var rs = NewChannelSession();
        rs.ConfigureCores(new[] { "ChannelCore", "ExpressCore" }, "ChannelCore");
        Assert.Contains("switch_core", rs.ActiveTools);
    }

    [Fact]
    public async Task Persistence_RoundTrips_ActiveCoreConfig()
    {
        using var _ = PointToTempStorage();
        var sessionId = "coretest1";
        var persister = new SessionPersistence(sessionId);
        await persister.SaveSpecAsync(new CreateSessionSpec { Type = "channel" }, DateTime.UtcNow);

        await persister.SaveStateAsync(new SessionStateSnapshot
        {
            SessionId = sessionId,
            Status = SessionStatus.Running,
            ActiveCoreConfig = "ExpressCore"
        });

        var loaded = persister.Load().State;
        Assert.Equal("ExpressCore", loaded.ActiveCoreConfig);

        // 旧存档（无该字段）回退空串，不抛错
        await persister.SaveStateAsync(new SessionStateSnapshot
        {
            SessionId = sessionId,
            Status = SessionStatus.Running
        });
        var loaded2 = persister.Load().State;
        Assert.Equal("", loaded2.ActiveCoreConfig);
    }

    [Fact]
    public async Task MarkCompletedForCleanup_PersistsCompletedStatus()
    {
        using var _ = PointToTempStorage();
        var sessionId = "cleanuptest1";
        var persister = new SessionPersistence(sessionId);
        await persister.SaveSpecAsync(new CreateSessionSpec { Type = "channel" }, DateTime.UtcNow);

        // 模拟测试运行中的 Running 会话
        var rs = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        rs.SetPersister(persister);
        await persister.SaveStateAsync(new SessionStateSnapshot
        {
            SessionId = sessionId,
            Status = SessionStatus.Running,
            ConnectedChannelId = "13"
        });

        // --test 退出清理：静默置 Completed + 落盘
        rs.MarkCompletedForCleanup();
        await rs.PersistStateAsync();

        var loaded = persister.Load().State;
        Assert.Equal(SessionStatus.Completed, loaded.Status);
    }

    [Fact]
    public void SwitchCore_PreservesSessionPrompt()
    {
        using var _ = PointToTempStorage();
        // 布置临时 Storage：会话类型配置声明 Prompts + 提示词文件
        var sessionDir = Path.Combine(PathConfig.StoragePath, "SessionTypes");
        var promptsDir = Path.Combine(sessionDir, "Prompts");
        Directory.CreateDirectory(promptsDir);
        File.WriteAllText(Path.Combine(sessionDir, "channel.json"),
            "{\"Type\":\"channel\",\"Core\":\"ChannelCore\"," +
            "\"AllowedCores\":[\"ChannelCore\",\"ExpressCore\"],\"Prompts\":[\"test_prompt.txt\"]}");
        File.WriteAllText(Path.Combine(promptsDir, "test_prompt.txt"), "测试会话提示词：channel 身份");

        var rs = NewChannelSession();
        rs.ConfigureCores(new[] { "ChannelCore", "ExpressCore" }, "ChannelCore");
        Assert.NotNull(rs.SessionPrompt);
        Assert.Contains("channel 身份", rs.SessionPrompt);

        // 换核不换提示词：切到 ExpressCore 后 SessionPrompt 仍来自 channel 类型配置
        rs.SwitchCore("ExpressCore");
        Assert.Contains("channel 身份", rs.SessionPrompt);
    }
}
