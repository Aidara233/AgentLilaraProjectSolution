using AgentLilara.GraphRuntime.Graph;
using Xunit;

namespace AgentLilara.GraphRuntime.Tests;

public sealed class PortTransferTests
{
    [Theory]
    [InlineData(CellState.Known)]
    [InlineData(CellState.NoEvent)]
    [InlineData(CellState.Unknown)]
    public void RecordsConsumedConnectionWithoutPayload(CellState state)
    {
        var value = state switch
        {
            CellState.Known => Cell.Of("private-body"),
            CellState.NoEvent => Cell.NoEvent,
            _ => Cell.Unknown
        };
        var graph = new GraphClass("flow", GraphKind.Static,
            new INode[] { new OnClock("on"), new ConstValue("source", value), new WriteVariable("sink", "x") },
            new[] { new Link("source", "value", "sink", "value") });
        var context = new EvalContext("trace", 7);
        Solver.Solve(graph, context);
        var transfer = Assert.Single(context.PortTransfers);
        Assert.Equal(state, transfer.State);
        Assert.Equal("source", transfer.FromNode);
        Assert.Equal("sink", transfer.ToNode);
        Assert.Equal(0, transfer.Order);
        Assert.Equal(state == CellState.Known ? typeof(string).FullName : null, transfer.PayloadType);
        Assert.DoesNotContain("private-body", System.Text.Json.JsonSerializer.Serialize(transfer));
    }

    [Fact]
    public void ThrowingNodeRetainsItsIdentityAndConsumedInputs()
    {
        var graph = new GraphClass("throws", GraphKind.Static,
            new INode[] { new OnClock("on"), new ThrowingNode() },
            new[] { new Link("on", "event", "broken", "event") });
        var context = new EvalContext("exception", 9);
        Assert.Throws<InvalidOperationException>(() => Solver.Solve(graph, context));
        Assert.Equal("broken", context.EvaluatingNode);
        Assert.Equal(new[] { "on" }, context.EvaluationOrder);
        Assert.Equal("broken", Assert.Single(context.PortTransfers).ToNode);
    }

    private sealed class ThrowingNode : INode
    {
        public string Id => "broken";
        public NodeKind Kind => NodeKind.Logic;
        public IReadOnlyList<string> InputPorts => new[] { "event" };
        public IReadOnlyList<string> OutputPorts => Array.Empty<string>();
        public IReadOnlyDictionary<string, Cell> Evaluate(IReadOnlyDictionary<string, Cell> inputs, EvalContext context)
            => throw new InvalidOperationException("test failure");
    }

    [Fact]
    public void FailedCycleOnlyReportsConnectionsActuallyConsumed()
    {
        var graph = new GraphClass("cycle", GraphKind.Static,
            new INode[] { new OnClock("on"), new ConstValue("c", Cell.Of(true)), new LogicAnd("a"), new LogicAnd("b") },
            new[] { new Link("c", "value", "a", "a"), new Link("b", "result", "a", "b"),
                new Link("c", "value", "b", "a"), new Link("a", "result", "b", "b") });
        var context = new EvalContext("failed", 8);
        var result = Solver.Solve(graph, context);
        Assert.False(result.Success);
        Assert.Equal(new[] { "a", "b" }, result.Unresolved);
        Assert.Equal(new[] { "on", "c" }, context.EvaluationOrder);
        Assert.Empty(context.PortTransfers);
    }
}
