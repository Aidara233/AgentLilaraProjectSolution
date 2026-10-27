using System.Text.Json.Nodes;
using AgentCoreProcessor.Core;
using AgentCoreProcessor.Models;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class NativeToolCallHandlerTests
{
    private static void Feed(NativeToolCallHandler handler, StreamEventType type, string? content = null,
        string? name = null, string? id = null) => handler.OnEvent(new()
        { Type = type, Content = content, ToolName = name, ToolUseId = id });

    [Fact]
    public void FragmentedArgumentsUseSchemaOrderAndPreserveRawJsonAndThinking()
    {
        var handler = new NativeToolCallHandler(new() { new ToolDefinition
        {
            Name = "shell", Parameters = JsonNode.Parse("""{"properties":{"command":{},"missing":{},"count":{}}}""")!
        } });
        Feed(handler, StreamEventType.Thinking, "thinking ");
        Feed(handler, StreamEventType.ToolUseStart, name: "shell", id: "call-1");
        Feed(handler, StreamEventType.ToolUseDelta, "{\"count\":2,\"command\":\"echo ");
        Feed(handler, StreamEventType.ToolUseDelta, "hello\"}");
        Feed(handler, StreamEventType.ToolUseEnd);
        Feed(handler, StreamEventType.Text, "text");
        var result = handler.GetResult();
        var call = Assert.Single(result.Calls);
        Assert.Equal("call-1", call.ToolUseId);
        Assert.Equal(new[] { "echo hello", "", "2" }, call.Inputs);
        Assert.Equal("""{"count":2,"command":"echo hello"}""", call.RawInputJson);
        Assert.Equal("thinking text", result.Thinking);
    }

    [Fact]
    public void CallsResetArgumentsAndUnknownSchemaUsesInputPropertyOrder()
    {
        var handler = new NativeToolCallHandler(new());
        Feed(handler, StreamEventType.ToolUseStart, name: "unknown", id: "one");
        Feed(handler, StreamEventType.ToolUseDelta, """{"b":"second","a":null}""");
        Feed(handler, StreamEventType.ToolUseEnd);
        Feed(handler, StreamEventType.ToolUseStart, name: "empty", id: "two");
        Feed(handler, StreamEventType.ToolUseEnd);
        var calls = handler.GetResult().Calls;
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "second", "" }, calls[0].Inputs);
        Assert.Equal("{}", calls[1].RawInputJson);
        Assert.Empty(calls[1].Inputs);
    }

    [Fact]
    public void MalformedJsonFallsBackToRawInputAndExtraEndDoesNotDuplicateCall()
    {
        var handler = new NativeToolCallHandler(new());
        Feed(handler, StreamEventType.ToolUseStart, name: "broken");
        Feed(handler, StreamEventType.ToolUseDelta, "{invalid");
        Feed(handler, StreamEventType.ToolUseEnd);
        Feed(handler, StreamEventType.ToolUseEnd);
        var call = Assert.Single(handler.GetResult().Calls);
        Assert.Equal("{invalid", call.RawInputJson);
        Assert.Equal(new[] { "{invalid" }, call.Inputs);
    }
}
