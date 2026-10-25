using AgentLilara.KernelBridge;
using AgentLilara.SessionBrain;
using Lilara.Core.Kernel;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 内核侧转换/比较的契约。⚠️ 这些测试**不联网**、不建会话、不写数据根。
/// </summary>
public class KernelBridgeTests
{
    [Fact]
    public void Build_MapsTextMessagesAndTools()
    {
        var request = Request(
            messages: [new SessionBrainMessage("user", [new SessionBrainText("你好")])],
            tools: [new SessionBrainTool("speak", "说话", """{"type":"object"}""")]);

        var build = KernelRequestBuilder.Build(request);

        var message = Assert.IsType<TimelineEvent.MessageEvent>(Assert.Single(build.Request.Timeline));
        Assert.Equal(Speaker.User, message.From);
        Assert.Equal("你好", Assert.IsType<Block.TextBlock>(Assert.Single(message.Blocks)).Text);

        var tool = Assert.Single(build.Request.Tools);
        Assert.Equal("speak", tool.Name);
        Assert.Equal("""{"type":"object"}""", tool.ParametersSchemaJson);
        Assert.Equal("系统提示", Assert.IsType<Block.TextBlock>(
            Assert.Single(build.Request.Slots.System)).Text);
    }

    [Fact]
    public void Build_AssistantBecomesModelSpeaker()
    {
        var request = Request(messages:
        [
            new SessionBrainMessage("assistant", [new SessionBrainText("回答")]),
        ]);

        var build = KernelRequestBuilder.Build(request);

        var message = Assert.IsType<TimelineEvent.MessageEvent>(Assert.Single(build.Request.Timeline));
        Assert.Equal(Speaker.Model, message.From);
    }

    // ⚠️ ALP 把 N 个 tool_result 装进一条消息，内核落成独立事件——这条差异是**结构性的**，
    // 转换必须保留每一个调用 id，否则配对会断。
    [Fact]
    public void Build_ToolResultsBecomeSeparateEventsInOrder()
    {
        var request = Request(messages:
        [
            new SessionBrainMessage("assistant",
            [
                new SessionBrainToolUse("c1", "speak", """{"inputs":["hi"]}"""),
                new SessionBrainToolUse("c2", "wait", """{"inputs":[]}"""),
            ]),
            new SessionBrainMessage("user",
            [
                new SessionBrainToolResult("c1", "ok", IsError: false),
                new SessionBrainToolResult("c2", "timeout", IsError: true),
            ]),
        ]);

        var build = KernelRequestBuilder.Build(request);

        // 1 条模型消息（含两条 tool_use）+ 2 条独立结果事件。只装结果的 user 消息不产生空事件。
        Assert.Equal(3, build.Request.Timeline.Count);
        Assert.IsType<TimelineEvent.MessageEvent>(build.Request.Timeline[0]);
        var r1 = Assert.IsType<TimelineEvent.ToolResultEvent>(build.Request.Timeline[1]);
        var r2 = Assert.IsType<TimelineEvent.ToolResultEvent>(build.Request.Timeline[2]);
        Assert.Equal("c1", r1.CallId);
        Assert.Equal("c2", r2.CallId);
        // IsError 内核没有对应字段——必须出现在能力缺口里，不能静默丢弃。
        Assert.Contains(build.Unsupported, note => note.Contains("IsError"));
    }

    [Fact]
    public void Build_ImageHashesTheActualBytes()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var request = Request(messages:
        [
            new SessionBrainMessage("user",
            [
                new SessionBrainImage(null, Convert.ToBase64String(bytes), "image/png"),
            ]),
        ]);

        var build = KernelRequestBuilder.Build(request);

        var message = Assert.IsType<TimelineEvent.MessageEvent>(Assert.Single(build.Request.Timeline));
        var image = Assert.IsType<Block.ImageBlock>(Assert.Single(message.Blocks));
        Assert.Equal(64, image.Sha256.Length);
        Assert.Equal("image/png", image.MediaType);
    }

    // 同一条请求走完「Legacy → DTO → 内核请求」之后，比较器不应报任何差异。
    [Fact]
    public void Compare_RoundTrip_IsEquivalent()
    {
        var request = Request(
            messages:
            [
                new SessionBrainMessage("user", [new SessionBrainText("第一问")]),
                new SessionBrainMessage("assistant", [new SessionBrainText("第一答")]),
                new SessionBrainMessage("user",
                [
                    new SessionBrainText("看图"),
                    new SessionBrainImage(null, Convert.ToBase64String(new byte[] { 9, 9 }), "image/png"),
                ]),
            ],
            tools: [new SessionBrainTool("read", "读文件", """{"type":"object"}""")]);

        var build = KernelRequestBuilder.Build(request);
        var result = CanonicalComparer.Compare(request, build.Request, build.Unsupported);

        Assert.Empty(result.Differences);
    }

    // 图片读不到字节时会被丢弃——比较器必须**看得见**，否则影子会报「等价」。
    [Fact]
    public void Compare_DroppedImage_IsReportedAsADifference()
    {
        var request = Request(messages:
        [
            new SessionBrainMessage("user",
            [
                new SessionBrainImage(@"Z:\definitely-missing\a.png", null, "image/png"),
            ]),
        ]);

        var build = KernelRequestBuilder.Build(request);
        var result = CanonicalComparer.Compare(request, build.Request, build.Unsupported);

        Assert.NotEmpty(result.Differences);
        Assert.Contains(result.Differences, d => d.Contains("timeline"));
    }

    [Fact]
    public void Compare_SystemRoleMessage_IsReportedNotSilentlyTreatedAsUser()
    {
        var request = Request(messages:
        [
            new SessionBrainMessage("system", [new SessionBrainText("[错误] 模型调用失败")]),
        ]);

        var build = KernelRequestBuilder.Build(request);
        var result = CanonicalComparer.Compare(request, build.Request, build.Unsupported);

        Assert.Contains(result.Differences, d => d.Contains("system"));
        Assert.Contains(result.Unsupported, note => note.Contains("role='system'"));
    }

    [Fact]
    public async Task ShadowBrain_ReportsAndNeverReturnsAContentReply()
    {
        var reports = new List<ShadowReport>();
        var brain = new KernelShadowBrain(reports.Add);

        var result = await brain.InvokeAsync(
            Request(messages: [new SessionBrainMessage("user", [new SessionBrainText("hi")])]),
            CancellationToken.None);

        Assert.Null(result.Text);
        Assert.Empty(result.ToolCalls);
        var report = Assert.Single(reports);
        Assert.True(report.Equivalent);
    }

    [Fact]
    public async Task ShadowBrain_WritesOneJsonLinePerCall()
    {
        var path = Path.Combine(Path.GetTempPath(), "lilara-shadow-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var brain = new KernelShadowBrain(reportPath: path);
            await brain.InvokeAsync(Request(messages: []), CancellationToken.None);

            var lines = File.ReadAllLines(path);
            Assert.Single(lines);
            Assert.Contains("\"AlpSessionId\"", lines[0]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static SessionBrainRequest Request(
        IReadOnlyList<SessionBrainMessage> messages,
        IReadOnlyList<SessionBrainTool>? tools = null) => new()
        {
            AlpSessionId = "s1",
            EngineType = "channel",
            WorkingDirectory = Path.GetTempPath(),
            SystemPrompt = "系统提示",
            Messages = messages,
            Tools = tools ?? [],
            Provider = new SessionBrainProvider
            {
                Provider = "claude",
                Endpoint = "https://example.invalid",
                ApiKey = "k",
                Model = "claude-opus-5",
                Temperature = 0.7,
                MaxTokens = 8192,
            },
        };
}
