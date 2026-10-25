using AgentCoreProcessor.Client;
using AgentCoreProcessor.Engine.Session.Brain;
using AgentCoreProcessor.Models;
using AgentLilara.SessionBrain;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 接缝的宿主侧契约：Legacy 请求如何被转成中性 DTO，以及接缝在 Off / Shadow 下做什么。
/// ⚠️ 这些测试**不**验证内核实现——那是 KernelBridge 自己的测试。
/// </summary>
public class SessionBrainTests
{
    // ---- Legacy → 中性 DTO ----

    [Fact]
    public void ToMessage_PlainContent_BecomesSingleTextPart()
    {
        var message = new Message { Role = "user", Content = "你好", Name = "alice" };

        var dto = LegacyRequestMapper.ToMessage(message);

        Assert.Equal("user", dto.Role);
        Assert.Equal("alice", dto.Name);
        var text = Assert.IsType<SessionBrainText>(Assert.Single(dto.Parts));
        Assert.Equal("你好", text.Text);
    }

    [Fact]
    public void ToMessage_ContentParts_TakePrecedenceOverContent()
    {
        var message = new Message
        {
            Role = "assistant",
            Content = "should be ignored",
            ContentParts =
            [
                ContentPart.FromText("先说一句"),
                ContentPart.FromToolUse("call-1", "speak", """{"inputs":["hi"]}"""),
            ],
        };

        var dto = LegacyRequestMapper.ToMessage(message);

        Assert.Equal(2, dto.Parts.Count);
        Assert.Equal("先说一句", Assert.IsType<SessionBrainText>(dto.Parts[0]).Text);
        var toolUse = Assert.IsType<SessionBrainToolUse>(dto.Parts[1]);
        Assert.Equal("call-1", toolUse.ToolUseId);
        Assert.Equal("speak", toolUse.ToolName);
    }

    [Fact]
    public void ToPart_ToolResult_KeepsErrorFlag()
    {
        var part = ContentPart.FromToolResult("call-1", "失败原因", isError: true);

        var dto = Assert.IsType<SessionBrainToolResult>(LegacyRequestMapper.ToPart(part));

        Assert.Equal("call-1", dto.ToolUseId);
        Assert.Equal("失败原因", dto.Content);
        Assert.True(dto.IsError);
    }

    [Fact]
    public void ToPart_Image_KeepsBothPathAndBase64()
    {
        var part = new ContentPart
        {
            Type = "image",
            ImagePath = @"C:\tmp\a.png",
            ImageBase64 = "AAAA",
            MediaType = "image/png",
        };

        var dto = Assert.IsType<SessionBrainImage>(LegacyRequestMapper.ToPart(part));

        Assert.Equal(@"C:\tmp\a.png", dto.Path);
        Assert.Equal("AAAA", dto.Base64);
        Assert.Equal("image/png", dto.MediaType);
    }

    // ⚠️ 未知部件类型不能被静默丢掉：影子比较必须能看见「有东西没过去」。
    [Fact]
    public void ToPart_UnknownType_IsPreservedNotDropped()
    {
        var dto = LegacyRequestMapper.ToPart(new ContentPart { Type = "audio", Text = "raw" });

        var text = Assert.IsType<SessionBrainText>(dto);
        Assert.Contains("audio", text.Text);
    }

    [Fact]
    public void ToProvider_MapsEveryFieldTheBridgeNeeds()
    {
        var config = new ApiClientCfg
        {
            Provider = "claude",
            ApiEndpoint = "https://example.invalid/v1",
            ApiKey = "k",
            Model = "claude-opus-5",
            Temperature = 0.3,
            MaxTokens = 4096,
            UseNativeTools = true,
            ForceToolCall = true,
            DisableImageMessages = true,
            EnableCaching = true,
            ExtraBody = new Dictionary<string, object> { ["top_k"] = 5 },
        };

        var dto = LegacyRequestMapper.ToProvider(config);

        Assert.Equal("claude", dto.Provider);
        Assert.Equal("https://example.invalid/v1", dto.Endpoint);
        Assert.Equal("claude-opus-5", dto.Model);
        Assert.Equal(0.3, dto.Temperature);
        Assert.Equal(4096, dto.MaxTokens);
        Assert.True(dto.UseNativeTools);
        Assert.True(dto.ForceToolCall);
        Assert.True(dto.DisableImageMessages);
        Assert.True(dto.EnableCaching);
        Assert.Equal("5", dto.ExtraBody!["top_k"]);
    }

    // ---- 接缝运行器 ----

    [Fact]
    public void Host_Off_DoesNothing()
    {
        SessionBrainHost.Off.RunShadow(Request());
        Assert.False(SessionBrainHost.Off.IsEnabled);
    }

    [Fact]
    public void Host_ShadowWithoutKernel_IsRejectedInsteadOfSilentlyOff()
    {
        Assert.Throws<ArgumentException>(() => new SessionBrainHost(SessionBrainMode.Shadow, kernel: null));
    }

    // ⚠️ 配置写错**启动即抛**：静默退回 Off 会让人以为影子在跑，而实际一条报告都没有。
    [Theory]
    [InlineData(null, SessionBrainMode.Off)]
    [InlineData("", SessionBrainMode.Off)]
    [InlineData("off", SessionBrainMode.Off)]
    [InlineData("SHADOW", SessionBrainMode.Shadow)]
    public void ParseMode_ValidValues(string? raw, SessionBrainMode expected)
    {
        Assert.Equal(expected, SessionBrainWiring.ParseMode(raw));
    }

    [Theory]
    [InlineData("live")]
    [InlineData("half")]
    public void ParseMode_InvalidOrUnimplemented_Throws(string raw)
    {
        Assert.Throws<InvalidOperationException>(() => SessionBrainWiring.ParseMode(raw));
    }

    [Fact]
    public async Task Host_Shadow_InvokesTheKernel()
    {
        var brain = new RecordingBrain();
        using var host = new SessionBrainHost(SessionBrainMode.Shadow, brain, maxConcurrentShadows: 1);
        var request = Request();

        host.RunShadow(request);

        Assert.Same(request, await brain.Received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Host_ShadowFailure_IsReportedAndNeverThrown()
    {
        var brain = new RecordingBrain { Failure = new InvalidOperationException("boom") };
        var reports = new List<(string Message, Exception? Error)>();
        using var host = new SessionBrainHost(
            SessionBrainMode.Shadow, brain, (message, error) => reports.Add((message, error)),
            maxConcurrentShadows: 1);

        host.RunShadow(Request());
        await brain.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (reports.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Single(reports);
        Assert.IsType<InvalidOperationException>(reports[0].Error);
    }

    private static SessionBrainRequest Request() => new()
    {
        AlpSessionId = "s1",
        EngineType = "channel",
        WorkingDirectory = Path.GetTempPath(),
        SystemPrompt = "system",
        Messages = [],
        Tools = [],
        Provider = new SessionBrainProvider
        {
            Provider = "claude",
            Endpoint = "https://example.invalid",
            ApiKey = "k",
            Model = "m",
            Temperature = 1,
        },
    };

    private sealed class RecordingBrain : ISessionBrain
    {
        public string Engine => "recording";
        public Exception? Failure { get; init; }
        public TaskCompletionSource<SessionBrainRequest> Received { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SessionBrainResult> InvokeAsync(SessionBrainRequest request, CancellationToken cancellationToken)
        {
            Received.TrySetResult(request);
            if (Failure is not null) throw Failure;
            return Task.FromResult(new SessionBrainResult(null, null, [], null));
        }
    }
}
