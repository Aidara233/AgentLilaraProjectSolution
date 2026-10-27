using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Context;
using AgentCoreProcessor.Util;
using AgentLilara.GraphRuntime.Graph;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class ContextShadowGraphTests
{
    private sealed record Input(DateTime Time, string Sender, string Text, string? Id,
        string? Reply, bool Bot, bool Mention, DateTime? PreviousDate);
    private sealed record Output(string Role, string Content, DateTime? NextDate);

    private sealed class Source(Input input) : INode
    {
        public string Id => "source";
        public NodeKind Kind => NodeKind.Source;
        public bool IsActivationSource => true;
        public IReadOnlyList<string> InputPorts => Array.Empty<string>();
        public IReadOnlyList<string> OutputPorts => new[] { "input" };
        public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> values, EvalContext context) =>
            new Dictionary<string, Cell> { ["input"] = Cell.Of(input) };
    }

    // Independent oracle candidate: no renderer, database, media or clock calls.
    private sealed class Format(bool corrupt = false) : INode
    {
        public string Id => "format";
        public NodeKind Kind => NodeKind.Logic;
        public IReadOnlyList<string> InputPorts => new[] { "input" };
        public IReadOnlyList<string> OutputPorts => new[] { "result" };
        public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> values, EvalContext context)
        {
            var input = values["input"].As<Input>()!;
            var date = input.PreviousDate;
            var lines = new List<string> { "[新消息]" };
            var clock = "";
            if (input.Time != default)
            {
                if (date.HasValue && date.Value.Date != input.Time.Date)
                    lines.Add(input.Time.ToString("yyyy-M-d"));
                date = input.Time.Date;
                clock = input.Time.ToString("[HH:mm]");
            }
            if (input.Mention) lines.Add("⚠️ [!! @提及 !!] ⚠️");
            var identity = input.Id is { Length: > 0 } ? "[#" + input.Id + "]" : "";
            var reply = input.Reply is { Length: > 0 } ? " (回复 #" + input.Reply + ")" : "";
            lines.Add(clock + identity + (input.Bot ? "你" : input.Sender) + ": " + input.Text + reply);
            var result = new Output("user", string.Join("\n", lines) + (corrupt ? "!" : ""), date);
            return new Dictionary<string, Cell> { ["result"] = Cell.Of(result) };
        }
    }

    private sealed class Capture : INode, ISinkExecutor
    {
        public Output? Result { get; private set; }
        public string Id => "capture";
        public NodeKind Kind => NodeKind.Sink;
        public IReadOnlyList<string> InputPorts => new[] { "result" };
        public IReadOnlyList<string> OutputPorts => Array.Empty<string>();
        public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> values, EvalContext context)
        {
            context.SinkCandidates.Add(new(Id, values, true));
            return new Dictionary<string, Cell>();
        }
        public SinkOutcome Execute(IReadOnlyDictionary<string, Cell> values, EvalContext context)
        {
            Result = values["result"].As<Output>();
            return SinkOutcome.Ok(Id, new Dictionary<string, Cell>());
        }
    }

    private static Output Run(Input input, bool corrupt = false)
    {
        var capture = new Capture();
        var graph = new GraphClass("shadow.context", GraphKind.Static,
            new INode[] { new Source(input), new Format(corrupt), capture },
            new[] { new Link("source", "input", "format", "input"), new Link("format", "result", "capture", "result") });
        var context = new EvalContext("shadow", 1);
        var instance = new GraphInstance(graph);
        Assert.True(instance.Run(context).Success);
        Assert.Equal(new[] { "source", "format", "capture" }, context.EvaluationOrder);
        Assert.Equal(2, context.PortTransfers.Count);
        Assert.All(context.PortTransfers, transfer => Assert.Equal(CellState.Known, transfer.State));
        Assert.Equal("source", context.PortTransfers[0].FromNode);
        Assert.Equal("format", context.PortTransfers[0].ToNode);
        Assert.Equal("capture", context.PortTransfers[1].ToNode);
        Assert.True(Assert.Single(instance.LastSinkOutcomes).Success);
        return Assert.IsType<Output>(capture.Result);
    }

    private sealed class NoMedia : IChannelMediaSource
    {
        public Task<string?> GetModelInputPathAsync(string hash) => throw new InvalidOperationException("unexpected media IO");
        public Task<string?> GetDescriptionAsync(string hash) => throw new InvalidOperationException("unexpected media IO");
        public Task<ChannelOcrSnapshot?> GetOcrAsync(string hash) => throw new InvalidOperationException("unexpected media IO");
        public int GetOcrRichTextThreshold() => throw new InvalidOperationException("unexpected media IO");
    }

    private static void AssertState(ChannelContextState state, DateTime? date)
    {
        Assert.Equal(42, state.ChannelId);
        Assert.Equal("group_42", state.ChannelName);
        Assert.Equal("session", state.Mode);
        Assert.Equal("fixed", state.FixedPrefix);
        Assert.Equal("summary", state.ContextSummary);
        Assert.Equal(17, state.LastConsumedMessageId);
        Assert.Equal(19, state.StartInjectMaxId);
        Assert.Equal(date, state.LastRenderedLocalDate);
    }

    private static ChannelContextState State(DateTime? date) => new()
    {
        ChannelId = 42, ChannelName = "group_42", Mode = "session", FixedPrefix = "fixed",
        ContextSummary = "summary", LastConsumedMessageId = 17, StartInjectMaxId = 19,
        LastRenderedLocalDate = date
    };

    [Theory]
    [InlineData(0, false, false, null, false)]
    [InlineData(1, false, false, null, false)]
    [InlineData(2, true, false, null, false)]
    [InlineData(3, false, true, "bot", false)]
    [InlineData(3, false, false, " bot", false)]
    [InlineData(3, false, false, "other,bot", true)]
    public async Task IndependentGraphMatchesOldIncrementalOutput(int timeCase, bool portMention,
        bool bot, string? mentions, bool reply)
    {
        var time = timeCase == 0 ? default : new DateTime(2026, 2, timeCase == 3 ? 4 : 3, 14, 5, 0, DateTimeKind.Local);
        var previous = timeCase < 2 ? (DateTime?)null : new DateTime(2026, 2, 3);
        var state = State(previous);
        state.RoundImageHashes.Add("already-seen");
        state.InjectedDescriptions.Add("description");
        state.InjectedOcrTexts.Add("ocr");
        var message = new UserMessage { Id = 20, ChannelId = 42, SenderName = "甲<&\"", Content = "正文\n<&\"",
            Time = time, IsFromBot = bot, PlatformMessageId = "m1", ReplyToPlatformMessageId = reply ? "r1" : null,
            MentionedPlatformIds = mentions };
        var service = new ChannelContextService(new FakeDataSource { BotPlatformId = "bot" }, new NoMedia());
        var actual = await service.FormatIncomingAsync(state, message, portMention);
        var expected = Run(new(time == default ? default : TimeUtil.ToLocalSafe(time), message.SenderName,
            message.Content, message.PlatformMessageId, message.ReplyToPlatformMessageId, bot,
            portMention || (mentions?.Split(',').Contains("bot") ?? false), previous));
        Assert.Equal(expected.Role, actual!.Role);
        Assert.Equal(expected.Content, actual.Content);
        Assert.Null(actual.Name);
        Assert.Null(actual.ContentParts);
        Assert.Equal(expected.NextDate, state.LastRenderedLocalDate);
        Assert.Equal(17, state.LastConsumedMessageId);
        Assert.Equal(19, state.StartInjectMaxId);
        Assert.Equal("group_42", state.ChannelName);
        Assert.Equal("session", state.Mode);
        Assert.Equal("fixed", state.FixedPrefix);
        Assert.Equal("summary", state.ContextSummary);
        Assert.Equal(new[] { "already-seen" }, state.RoundImageHashes);
        Assert.Equal(new[] { "description" }, state.InjectedDescriptions);
        Assert.Equal(new[] { "ocr" }, state.InjectedOcrTexts);
    }

    [Fact]
    public async Task ConsecutiveMessagesAdvanceIndependentStatesAndNegativeControlDiffers()
    {
        var state = State(null);
        DateTime? graphDate = null;
        var service = new ChannelContextService(new FakeDataSource(), new NoMedia());
        foreach (var time in new[] { new DateTime(2026, 2, 3, 23, 58, 0, DateTimeKind.Local),
            new DateTime(2026, 2, 3, 23, 59, 0, DateTimeKind.Local), default,
            new DateTime(2026, 2, 4, 0, 1, 0, DateTimeKind.Local) })
        {
            var message = new UserMessage { SenderName = "甲", Content = "hello", Time = time };
            var input = new Input(time == default ? default : TimeUtil.ToLocalSafe(time), "甲", "hello", null, null, false, false, graphDate);
            var graph = Run(input);
            var legacy = await service.FormatIncomingAsync(state, message);
            Assert.Equal(legacy!.Content, graph.Content);
            AssertState(state, graph.NextDate);
            Assert.Equal("user", legacy.Role);
            Assert.Null(legacy.Name);
            Assert.Null(legacy.ContentParts);
            Assert.Empty(state.RoundImageHashes);
            Assert.Empty(state.InjectedDescriptions);
            Assert.Empty(state.InjectedOcrTexts);
            Assert.NotEqual(legacy.Content, Run(input, corrupt: true).Content);
            graphDate = graph.NextDate;
        }
    }
}
