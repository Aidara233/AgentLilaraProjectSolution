using System.Reflection;
using AgentCoreProcessor.Engine.Session;
using AgentCoreProcessor.Tool.Core;
using AgentLilara.PluginSDK;
using Xunit;

namespace AgentCoreProcessor.Tests;

public sealed class TransitionOperationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CompactRejectsMissingSummaryWithoutCallingOperation(string summary)
    {
        var calls = 0;
        var tool = new CompactTool("session", _ => { calls++; return Task.CompletedTask; });
        var result = await tool.ExecuteAsync(new() { summary }, default);
        Assert.Equal("failed", result.Status);
        Assert.Equal(0, calls);
        Assert.Equal("failed", (await tool.ExecuteAsync(new(), default)).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompactAwaitsOperationAndPreservesFailureText(bool fails)
    {
        string? captured = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new CompactTool("session", summary => { captured = summary; return completion.Task; });
        var running = tool.ExecuteAsync(new() { " summary " }, default);
        Assert.False(running.IsCompleted);
        Assert.Equal(" summary ", captured);
        if (fails) completion.SetException(new InvalidOperationException("transition failure"));
        else completion.SetResult();
        var result = await running;
        Assert.Equal(fails ? "failed" : "success", result.Status);
        if (fails) Assert.Equal("compact 失败：transition failure", result.Error);
    }

    [Theory]
    [InlineData("express", true, false, true)]
    [InlineData("express", true, true, false)]
    [InlineData("express", false, false, false)]
    [InlineData("channel", true, false, false)]
    public async Task EscalationUsesInjectedOperationAndRetainsSessionOnFailure(string type, bool supplied, bool fails, bool ends)
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = type }, new[] { "basic-tools" });
        string? captured = null;
        if (supplied) session.SetTransitionOperations(_ => Task.CompletedTask, reason =>
        {
            captured = reason;
            return fails ? Task.FromException(new InvalidOperationException("failure")) : Task.CompletedTask;
        });
        var method = typeof(RuntimeSession).GetMethod("ApplyToolEffectAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var effect = await (Task<ToolEffect>)method.Invoke(session, new object[] {
            new ToolCall { Tool = "escalate" }, new ToolResult { Status = "success", Data = "reason" } })!;
        Assert.Equal(ends, effect.EndSession);
        Assert.Equal(type == "express" && supplied ? "reason" : null, captured);
    }

    [Fact]
    public void CompressionOnlyAddsCompactWhenOperationIsAvailable()
    {
        var session = new RuntimeSession(new CreateSessionSpec { Type = "channel" }, new[] { "basic-tools" });
        session.InitCompression();
        Assert.DoesNotContain("compact", session.ActiveTools);
        session.SetTransitionOperations(_ => Task.CompletedTask, _ => Task.CompletedTask);
        session.InitCompression();
        session.InitCompression();
        Assert.Single(session.ActiveTools, name => name == "compact");
        Assert.DoesNotContain(typeof(RuntimeSession).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(SessionEngine));
        Assert.DoesNotContain(typeof(CompactTool).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            field => field.FieldType == typeof(SessionEngine) || field.FieldType == typeof(RuntimeSession));
    }
}
