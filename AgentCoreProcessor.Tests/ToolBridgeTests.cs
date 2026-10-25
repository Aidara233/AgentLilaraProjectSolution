using AgentLilara.AlcBridge;
using AlpITool = AgentLilara.PluginSDK.ITool;
using AlpToolParameter = AgentLilara.PluginSDK.ToolParameter;
using AlpToolResult = AgentLilara.PluginSDK.ToolResult;
using AlpContentAttachment = AgentLilara.PluginSDK.ContentAttachment;
using Lilara.Core.Kernel;
using Xunit;

namespace AgentCoreProcessor.Tests;

/// <summary>
/// 工具桥：ALP 的「命名 JSON + 位置化 inputs + ToolResult」↔ ALC 的
/// 「原始 JSON + ToolOutcome」。⚠️ 不执行真实工具、不联网。
/// </summary>
public class ToolBridgeTests
{
    [Fact]
    public void Wrap_DetectsDuplicateNames()
    {
        var catalog = ToolBridge.Wrap(
        [
            new FakeTool { Name = "demo" },
            new FakeTool { Name = "demo" },
        ]);

        Assert.Single(catalog.Tools);
        Assert.Single(catalog.Conflicts);
        Assert.Contains("demo", catalog.Conflicts[0]);
    }

    [Fact]
    public void Wrap_HonoursVisibilityPredicate()
    {
        var catalog = ToolBridge.Wrap(
        [
            new FakeTool { Name = "visible" },
            new FakeTool { Name = "hidden" },
        ],
            visible: tool => tool.Name != "hidden");

        Assert.Equal(["visible"], catalog.Tools.Select(t => t.Name));
    }

    [Fact]
    public void Wrap_NotesTheDefaultPermissionOnce()
    {
        var catalog = ToolBridge.Wrap([new FakeTool(), new FakeTool { Name = "b" }]);

        Assert.All(catalog.Tools, t => Assert.Equal(PermissionClass.Writing, t.PermissionClass));
        Assert.Single(catalog.Notes);
    }

    [Fact]
    public void Wrap_ExplicitPermissionWins()
    {
        var catalog = ToolBridge.Wrap(
            [new FakeTool()],
            permission: _ => PermissionClass.ReadOnly);

        Assert.Equal(PermissionClass.ReadOnly, catalog.Tools[0].PermissionClass);
        Assert.Empty(catalog.Notes);
    }

    [Fact]
    public void Spec_SchemaComesFromTheAlpToolVerbatim()
    {
        var catalog = ToolBridge.Wrap([new FakeTool()]);

        var schema = catalog.Tools[0].ParametersSchemaJson;
        Assert.Contains("\"command\"", schema);
        Assert.Contains("\"required\"", schema);
    }

    [Fact]
    public async Task Run_MapsNamedJsonToPositionalInputsInIndexOrder()
    {
        var seen = new List<string>();
        var tool = new FakeTool
        {
            Parameters =
            [
                new AlpToolParameter("command", "命令", index: 0),
                new AlpToolParameter("timeout", "超时", index: 1, isRequired: false),
            ],
            Execute = (inputs, _) =>
            {
                seen.AddRange(inputs);
                return Task.FromResult(new AlpToolResult { Status = "success", Data = "ok" });
            },
        };

        var adapter = ToolBridge.Wrap([tool]).Tools[0];
        await adapter.RunAsync(
            new ToolCall("c1", "demo", """{"command":"ls -la","timeout":5}"""),
            new FakeContext(), CancellationToken.None);

        Assert.Equal(["ls -la", "5"], seen);
    }

    [Fact]
    public async Task Run_KeepsIndexOrderEvenWhenJsonOrderDiffers()
    {
        var seen = new List<string>();
        var tool = new FakeTool
        {
            Parameters =
            [
                new AlpToolParameter("first", "第一", index: 0),
                new AlpToolParameter("second", "第二", index: 1),
            ],
            Execute = (inputs, _) =>
            {
                seen.AddRange(inputs);
                return Task.FromResult(new AlpToolResult { Status = "success", Data = "ok" });
            },
        };

        var adapter = ToolBridge.Wrap([tool]).Tools[0];
        await adapter.RunAsync(
            new ToolCall("c1", "demo", """{"second":"B","first":"A"}"""),
            new FakeContext(), CancellationToken.None);

        Assert.Equal(["A", "B"], seen);
    }

    [Fact]
    public async Task Run_MissingPropertyBecomesEmptyString()
    {
        var seen = new List<string>();
        var tool = new FakeTool
        {
            Parameters =
            [
                new AlpToolParameter("a", "A", index: 0),
                new AlpToolParameter("b", "B", index: 1),
            ],
            Execute = (inputs, _) =>
            {
                seen.AddRange(inputs);
                return Task.FromResult(new AlpToolResult { Status = "success", Data = "ok" });
            },
        };

        var adapter = ToolBridge.Wrap([tool]).Tools[0];
        await adapter.RunAsync(
            new ToolCall("c1", "demo", """{"a":"x"}"""), new FakeContext(), CancellationToken.None);

        Assert.Equal(["x", ""], seen);
    }

    [Fact]
    public async Task Run_FailureBecomesContentNotEmpty()
    {
        var tool = new FakeTool
        {
            Execute = (_, _) => Task.FromResult(new AlpToolResult
            {
                Status = "failed",
                Error = "文件不存在",
            }),
        };

        var adapter = ToolBridge.Wrap([tool]).Tools[0];
        var outcome = await adapter.RunAsync(
            new ToolCall("c1", "demo", "{}"), new FakeContext(), CancellationToken.None);

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content));
        Assert.Contains("failed", text.Text);
        Assert.Contains("文件不存在", text.Text);
    }

    [Fact]
    public async Task Run_ExceptionBecomesContent()
    {
        var tool = new FakeTool
        {
            Execute = (_, _) => throw new InvalidOperationException("炸了"),
        };

        var adapter = ToolBridge.Wrap([tool]).Tools[0];
        var outcome = await adapter.RunAsync(
            new ToolCall("c1", "demo", "{}"), new FakeContext(), CancellationToken.None);

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content));
        Assert.Contains("炸了", text.Text);
    }

    // ⚠️ 超时必须变成**结果**：tool_use / tool_result 配对是上游硬要求。
    [Fact]
    public async Task Run_TimeoutBecomesContent()
    {
        var tool = new FakeTool
        {
            Timeout = TimeSpan.FromMilliseconds(30),
            Execute = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new AlpToolResult { Status = "success" };
            },
        };

        var adapter = ToolBridge.Wrap([tool]).Tools[0];
        var outcome = await adapter.RunAsync(
            new ToolCall("c1", "demo", "{}"), new FakeContext(), CancellationToken.None);

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content));
        Assert.Contains("超时", text.Text);
    }

    [Fact]
    public async Task Run_CallerCancellationPropagates()
    {
        var tool = new FakeTool
        {
            Timeout = TimeSpan.FromMinutes(5),
            Execute = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new AlpToolResult { Status = "success" };
            },
        };

        var adapter = ToolBridge.Wrap([tool]).Tools[0];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            adapter.RunAsync(new ToolCall("c1", "demo", "{}"), new FakeContext(), cts.Token));
    }

    [Fact]
    public async Task Run_ImageAttachmentIsStoredAndBecomesAnImageBlock()
    {
        var context = new FakeContext();
        var tool = new FakeTool
        {
            Execute = (_, _) => Task.FromResult(new AlpToolResult
            {
                Status = "success",
                Data = "图在这里",
                Attachments =
                [
                    new AlpContentAttachment
                    {
                        Type = "image",
                        Base64Data = Convert.ToBase64String([1, 2, 3]),
                        MediaType = "image/png",
                    },
                ],
            }),
        };

        var adapter = ToolBridge.Wrap([tool]).Tools[0];
        var outcome = await adapter.RunAsync(
            new ToolCall("c1", "demo", "{}"), context, CancellationToken.None);

        Assert.Single(context.Stored);
        Assert.Equal([1, 2, 3], context.Stored[0]);
        var image = outcome.Content.OfType<Block.ImageBlock>().Single();
        Assert.Equal("image/png", image.MediaType);
    }

    // ⚠️ 附件既没有 base64 也读不到文件时**必须说出来**：静默丢 = 多一条假信息。
    [Fact]
    public async Task Run_UnreadableAttachmentBecomesExplicitText()
    {
        var tool = new FakeTool
        {
            Execute = (_, _) => Task.FromResult(new AlpToolResult
            {
                Status = "success",
                Attachments =
                [
                    new AlpContentAttachment { Type = "image", FilePath = @"Z:\missing.png" },
                ],
            }),
        };

        var adapter = ToolBridge.Wrap([tool]).Tools[0];
        var outcome = await adapter.RunAsync(
            new ToolCall("c1", "demo", "{}"), new FakeContext(), CancellationToken.None);

        var text = Assert.IsType<Block.TextBlock>(Assert.Single(outcome.Content));
        Assert.Contains("附件无法传递", text.Text);
    }

    // ⚠️ 宿主副作用（speak 文本 / RequestWait / end_session reason）读的是**原始** ToolResult，
    // 不是 ALC 的 ToolOutcome ⇒ 账本必须能原样交回，而且只能取一次。
    [Fact]
    public async Task Run_LedgerKeepsTheOriginalResultForHostEffects()
    {
        var original = new AlpToolResult
        {
            Status = "success",
            Data = "发出去的文本",
            RequestWait = true,
            WaitReason = "等人回复",
        };
        var tool = new FakeTool { Execute = (_, _) => Task.FromResult(original) };
        var ledger = new ToolResultLedger();

        var adapter = ToolBridge.Wrap([tool], ledger: ledger).Tools[0];
        await adapter.RunAsync(
            new ToolCall("c1", "demo", "{}"), new FakeContext(), CancellationToken.None);

        var taken = ledger.Take("c1");
        Assert.Same(original, taken);
        Assert.Equal("发出去的文本", taken!.Data);
        Assert.True(taken.RequestWait);
        Assert.Null(ledger.Take("c1"));   // 一次性
    }

    [Fact]
    public async Task Run_TimeoutAlsoRecordsAFailedResult()
    {
        var tool = new FakeTool
        {
            Timeout = TimeSpan.FromMilliseconds(30),
            Execute = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return new AlpToolResult { Status = "success" };
            },
        };
        var ledger = new ToolResultLedger();

        var adapter = ToolBridge.Wrap([tool], ledger: ledger).Tools[0];
        await adapter.RunAsync(
            new ToolCall("c1", "demo", "{}"), new FakeContext(), CancellationToken.None);

        var taken = ledger.Take("c1");
        Assert.NotNull(taken);
        Assert.Equal("failed", taken!.Status);
        Assert.Contains("超时", taken.Error);
    }

    private sealed class FakeTool : AlpITool
    {
        public string Name { get; init; } = "demo";
        public string Description => "demo tool";
        public IReadOnlyList<AlpToolParameter> Parameters { get; init; } =
            [new AlpToolParameter("command", "命令", index: 0)];
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

        public Func<List<string>, CancellationToken, Task<AlpToolResult>> Execute { get; init; } =
            (_, _) => Task.FromResult(new AlpToolResult { Status = "success", Data = "ok" });

        public Task<AlpToolResult> ExecuteAsync(List<string> resolvedInputs, CancellationToken ct) =>
            Execute(resolvedInputs, ct);
    }

    private sealed class FakeContext : IToolContext
    {
        public List<byte[]> Stored { get; } = [];

        public string WorkingDirectory => Path.GetTempPath();
        public string? ResolvePath(string relativePath) => relativePath;
        public byte[]? ReadBlob(string sha256) => null;
        public string EnsureStored(byte[] content)
        {
            Stored.Add(content);
            return new string('0', 64);
        }
        public IReadOnlyList<Block>? SnapshotOf(string stableId) => null;
        public byte[]? RawBytesOf(string stableId) => null;
        public string FloatingContext => "";
        public bool CompactRequested => false;
        public NetworkAccess? Network => null;
        public ShellAccess? Shell => null;
        public bool GoalActive => false;
    }
}
