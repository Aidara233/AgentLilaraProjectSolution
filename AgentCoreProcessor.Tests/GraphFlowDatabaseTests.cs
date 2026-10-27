using System.Reflection;
using AgentCoreProcessor.Engine.Graph;
using AgentCoreProcessor.Logging;
using AgentLilara.GraphRuntime.Contracts;
using AgentLilara.GraphRuntime.Graph;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class GraphFlowDatabaseTests
{
    [Fact]
    public async Task ShutdownDoesNotReplayAnAlreadyCommittedBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "writer-shutdown-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var database = new LogDatabase(root);
            var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = new LogWriter(database, new OpenSpanTracker(), new TokenAggregator(database));
            try
            {
                using var subscription = writer.Subscribe(_ => committed.TrySetResult());
                writer.Enqueue(new LogEvent
                {
                    SignalId = "shutdown-test", Scope = "test", GroupName = LogGroup.Engine,
                    Type = "event", Name = "once", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { writer.Dispose(); }
            Assert.Single(new LogQuery(database).GetBySignal("shutdown-test"));
        }
        finally
        {
            using (var poolKey = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "logs.db")}"))
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(poolKey);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RunnerPersistsFlowIdentityStateAndFailureNode(bool fails, bool sinkFails)
    {
        var root = Path.Combine(Path.GetTempPath(), "flow-db-" + Guid.NewGuid().ToString("N"));
        var writerField = typeof(SignalContext).GetField("_writer", BindingFlags.NonPublic | BindingFlags.Static)!;
        var levelField = typeof(SignalContext).GetField("_minLevel", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousWriter = writerField.GetValue(null);
        var previousLevel = levelField.GetValue(null);
        try
        {
            using var database = new LogDatabase(root);
            var writer = new LogWriter(database, new OpenSpanTracker(), new TokenAggregator(database));
            string signalId;
            try
            {
                SignalContext.Init(writer);
                using (var signal = Signal.Begin(LogGroup.Engine, "flow-test", "flow-test"))
                {
                    signalId = signal.SignalId;
                    var graph = new GraphClass("user.flow-test", GraphKind.Static,
                        new INode[] { new OnMessageEvent("in"), sinkFails ? new FailingSink() : new FlowNode(fails) },
                        new[] { new Link("in", "event", "consumer", "event") });
                    var runner = KeywordGraphRunner.ForInstance(new GraphInstance(graph, "instance-test"), false);
                    await runner.EvaluateAsync(InboundEvent.Message("test", "42", "private-body", ChannelKind.Group), "trace-test");
                }
            }
            finally
            {
                writerField.SetValue(null, previousWriter);
                levelField.SetValue(null, previousLevel);
                writer.Dispose();
            }

            var events = new LogQuery(database).GetBySignal(signalId);
            var beat = Assert.Single(events, e => e.Name == "graph.flow.beat");
            Assert.Equal("flow-test", beat.Scope);
            Assert.NotNull(beat.ParentId);
            var detail = JObject.Parse(beat.Detail!);
            Assert.Equal("user.flow-test", (string?)detail["classId"]);
            Assert.Equal("instance-test", (string?)detail["instanceKey"]);
            Assert.Equal("graph-keyword:trace-test", (string?)detail["traceId"]);
            Assert.True((long)detail["seq"]! > 0);
            Assert.Equal(!fails, (bool)detail["success"]!);
            Assert.Equal(!fails, (bool)detail["solveSuccess"]!);
            Assert.Equal(!fails && !sinkFails, (bool)detail["executionSuccess"]!);
            if (sinkFails)
            {
                var outcome = Assert.Single((JArray)detail["sinkOutcomes"]!);
                Assert.Equal("consumer", (string?)outcome["NodeId"]);
                Assert.False((bool)outcome["Success"]!);
                Assert.Equal(nameof(InvalidOperationException), (string?)outcome["ErrorType"]);
                Assert.Null((string?)outcome["error"]);
                Assert.DoesNotContain("private-sink-payload", beat.Detail!);
                Assert.Equal("consumer", (string?)Assert.Single((JArray)detail["firedSinks"]!));
            }
            else Assert.Empty((JArray)detail["sinkOutcomes"]!);
            var transfer = Assert.Single((JArray)detail["transfers"]!);
            Assert.Equal("in", (string?)transfer["FromNode"]);
            Assert.Equal("consumer", (string?)transfer["ToNode"]);
            Assert.Equal("Known", (string?)transfer["state"]);
            Assert.DoesNotContain("private-body", beat.Detail!);
            if (fails)
            {
                Assert.Equal("consumer", (string?)detail["evaluatingNode"]);
                Assert.Equal(nameof(InvalidOperationException), (string?)detail["errorType"]);
                Assert.Single((JArray)detail["evaluationOrder"]!);
            }
            else Assert.Equal(2, ((JArray)detail["evaluationOrder"]!).Count);
        }
        finally
        {
            writerField.SetValue(null, previousWriter);
            levelField.SetValue(null, previousLevel);
            using (var poolKey = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "logs.db")}"))
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(poolKey);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsecutiveBeatsKeepFailureAndSuppressionLedgersLocal(bool dynamic)
    {
        var root = Path.Combine(Path.GetTempPath(), "flow-beats-" + Guid.NewGuid().ToString("N"));
        var writerField = typeof(SignalContext).GetField("_writer", BindingFlags.NonPublic | BindingFlags.Static)!;
        var levelField = typeof(SignalContext).GetField("_minLevel", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previousWriter = writerField.GetValue(null);
        var previousLevel = levelField.GetValue(null);
        try
        {
            using var database = new LogDatabase(root);
            var writer = new LogWriter(database, new OpenSpanTracker(), new TokenAggregator(database));
            var sink = new CountingSink();
            var gate = new SwitchableNode();
            string signalId;
            try
            {
                SignalContext.Init(writer);
                using var signal = Signal.Begin(LogGroup.Engine, "two-beats", "two-beats");
                signalId = signal.SignalId;
                var graph = new GraphClass("user.two-beats", dynamic ? GraphKind.Dynamic : GraphKind.Static,
                    new INode[] { new OnMessageEvent("in"), gate, sink,
                        new IntentSink("notify", IntentCatalog.Find("sink.notify")!) },
                    new[] { new Link("in", "event", "gate", "event"),
                        new Link("gate", "event", "counter", "event"),
                        new Link("gate", "event", "notify", "event") });
                var runner = KeywordGraphRunner.ForInstance(new GraphInstance(graph), false);
                var input = InboundEvent.Message("test", "42", "hello", ChannelKind.Group);
                Assert.Single(await runner.EvaluateAsync(input, "first"));
                gate.Throws = !dynamic;
                Assert.Empty(await runner.EvaluateAsync(input, "second"));
            }
            finally
            {
                writerField.SetValue(null, previousWriter);
                levelField.SetValue(null, previousLevel);
                writer.Dispose();
            }
            var beats = new LogQuery(database).GetBySignal(signalId)
                .Where(e => e.Name == "graph.flow.beat").Select(e => JObject.Parse(e.Detail!))
                .ToDictionary(d => (string)d["traceId"]!);
            Assert.Equal(2, beats.Count);
            var first = beats["graph-keyword:first"];
            var second = beats["graph-keyword:second"];
            Assert.Equal(2, ((JArray)first["firedSinks"]!).Count);
            Assert.Single((JArray)first["sinkOutcomes"]!);
            Assert.Equal(1, (int)first["intentCount"]!);
            Assert.Equal(1, sink.Executions);
            Assert.Empty((JArray)second["firedSinks"]!);
            Assert.Empty((JArray)second["sinkOutcomes"]!);
            Assert.Equal(0, (int)second["intentCount"]!);
            Assert.Equal(dynamic, (bool)second["solveSuccess"]!);
            Assert.Equal(dynamic, (bool)second["executionSuccess"]!);
            if (dynamic)
                Assert.Equal(new[] { "counter", "notify" }, ((JArray)second["suppressedSinks"]!).Values<string>());
            else
            {
                Assert.Empty((JArray)second["suppressedSinks"]!);
                Assert.Equal("gate", (string?)second["evaluatingNode"]);
                Assert.Equal(nameof(InvalidOperationException), (string?)second["errorType"]);
            }
        }
        finally
        {
            writerField.SetValue(null, previousWriter);
            levelField.SetValue(null, previousLevel);
            using (var poolKey = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "logs.db")}"))
                Microsoft.Data.Sqlite.SqliteConnection.ClearPool(poolKey);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class SwitchableNode : INode
    {
        public bool Throws { get; set; }
        public string Id => "gate";
        public NodeKind Kind => NodeKind.Logic;
        public IReadOnlyList<string> InputPorts => new[] { "event" };
        public IReadOnlyList<string> OutputPorts => new[] { "event" };
        public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
        {
            if (Throws) throw new InvalidOperationException("second beat failure");
            return new Dictionary<string, Cell> { ["event"] = inputs["event"] };
        }
    }

    private sealed class CountingSink : INode, ISinkExecutor
    {
        public int Executions { get; private set; }
        public string Id => "counter";
        public NodeKind Kind => NodeKind.Sink;
        public IReadOnlyList<string> InputPorts => new[] { "event" };
        public IReadOnlyList<string> OutputPorts => Array.Empty<string>();
        public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
        {
            context.SinkCandidates.Add(new SinkCandidate(Id, inputs, true));
            return new Dictionary<string, Cell>();
        }
        public SinkOutcome Execute(IReadOnlyDictionary<string, Cell> args, EvalContext context)
        {
            Executions++;
            return SinkOutcome.Ok(Id, new Dictionary<string, Cell>());
        }
    }

    private sealed class FailingSink : INode, ISinkExecutor
    {
        public string Id => "consumer";
        public NodeKind Kind => NodeKind.Sink;
        public IReadOnlyList<string> InputPorts => new[] { "event" };
        public IReadOnlyList<string> OutputPorts => Array.Empty<string>();
        public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
        {
            context.SinkCandidates.Add(new SinkCandidate(Id, inputs, true));
            return new Dictionary<string, Cell>();
        }
        public SinkOutcome Execute(IReadOnlyDictionary<string, Cell> args, EvalContext context) =>
            throw new InvalidOperationException("private-sink-payload");
    }

    private sealed class FlowNode(bool fails) : INode
    {
        public string Id => "consumer";
        public NodeKind Kind => NodeKind.Logic;
        public IReadOnlyList<string> InputPorts => new[] { "event" };
        public IReadOnlyList<string> OutputPorts => Array.Empty<string>();
        public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
        {
            if (fails) throw new InvalidOperationException("test failure");
            return new Dictionary<string, Cell>();
        }
    }
}
